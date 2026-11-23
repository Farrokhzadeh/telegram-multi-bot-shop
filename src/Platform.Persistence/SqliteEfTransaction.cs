using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Runtime.CompilerServices;

namespace Platform.Persistence;

public sealed partial class SqliteSessionFactory
{
    private sealed class EfTransaction(bool write, int timeout)
    {
        internal readonly bool Write = write;
        internal readonly int Timeout = timeout;
        internal readonly EfIntegrityScope Changes = new();
        internal int Busy;
    }
    private static readonly ConditionalWeakTable<SqliteTransaction, EfTransaction> efTransactions = new();
    internal static async Task<T> StepEfAsync<T>(SqliteTransaction transaction, bool write, Func<EfSession, CancellationToken, Task<T>> operation,
        CancellationToken ct, WriterBinding? writer = null, bool validate = false)
    {
        if (!efTransactions.TryGetValue(transaction, out var state)) throw new InvalidOperationException("An owned SQLite transaction is required.");
        if (state.Changes.Rejected || (write && !state.Write)) { state.Changes.Rejected = true; throw new InvalidOperationException("SQLite EF transaction scope is unavailable."); }
        if (Interlocked.CompareExchange(ref state.Busy, 1, 0) != 0) { state.Changes.Rejected = true; throw new InvalidOperationException("SQLite EF steps cannot run concurrently."); }
        try
        {
            await using var context = new SqliteAkantiDbContext(new DbContextOptionsBuilder<SqliteAkantiDbContext>()
                .UseSqlite(transaction.Connection!, provider => provider.CommandTimeout(state.Timeout)).Options);
            await context.Database.UseTransactionAsync(transaction, ct);
            var session = new EfSession(context, write, writer, changes: state.Changes);
            session.InitializeIntegrity();
            T result = await operation(session, ct);
            await session.FlushAsync(ct, validate);
            return result;
        }
        catch { state.Changes.Rejected = true; throw; }
        finally { Interlocked.Exchange(ref state.Busy, 0); }
    }
    private static async Task ValidateEfAsync(SqliteTransaction transaction, CancellationToken ct)
    {
        var state = efTransactions.GetValue(transaction, _ => throw new InvalidOperationException("An owned SQLite transaction is required."));
        if (state.Changes.Rejected) throw new InvalidOperationException("The SQLite EF command was rejected and cannot commit.");
        if (state.Changes.Empty) return;
        await using var context = new SqliteAkantiDbContext(new DbContextOptionsBuilder<SqliteAkantiDbContext>()
            .UseSqlite(transaction.Connection!, provider => provider.CommandTimeout(state.Timeout)).Options);
        await context.Database.UseTransactionAsync(transaction, ct);
        context.Integrity = new EfIntegrity(context, () => false, state.Changes);
        await context.Integrity.ValidateAsync(ct);
    }
}
