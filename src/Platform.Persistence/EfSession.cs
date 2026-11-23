using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using Platform.Persistence.Configurations;
using Platform.Persistence.Entities;
using Platform.Security;

namespace Platform.Persistence;

internal sealed class EfSession(AkantiDbContext data, bool write, WriterBinding? writer, Action<BrandContext, long>? markWriter = null, Action? markShared = null)
{
    private bool admitted;
    internal void InitializeIntegrity() => _ = Integrity;
    internal AkantiDbContext Data { get; } = data;
    private EfIntegrity Integrity => Data.Integrity ??= new EfIntegrity(Data, () => write && admitted);
    private async Task<int> AdmitAsync(Func<Task<int>> operation)
    {
        Integrity.AllowSetWrite = true;
        try { return await operation(); }
        catch { Integrity.Reject(); throw; }
        finally { Integrity.AllowSetWrite = false; }
    }
    internal async Task<int> UpdateAsync<T>(Expression<Func<T, bool>> predicate, Action<UpdateSettersBuilder<T>> setters, CancellationToken ct) where T : class
    {
        Integrity.RequireWrite();
        try
        {
            if (typeof(T) != typeof(Wallet) && typeof(T) != typeof(Entities.User))
                throw new InvalidOperationException("The EF mutation type has no qualified affected-scope adapter.");
            if (Data.ChangeTracker.Entries<T>().Any()) throw new InvalidOperationException("Set-based updates require untracked entities.");
            var query = Data.Set<T>().Where(predicate);
            var key = Data.Model.FindEntityType(typeof(T))!.FindPrimaryKey()!.Properties.Single();
            var before = await query.AsNoTracking().Take(257).ToListAsync(ct);
            if (before.Count > 256) throw new InvalidOperationException("The EF mutation batch exceeds its bound.");
            Guid[] ids = before.Select(value => (Guid)Data.Entry(value).Property(key.Name).CurrentValue!).ToArray();
            if (ids.Length == 0) return 0;
            foreach (var value in before) Integrity.Capture(value);
            Integrity.AllowSetWrite = true;
            int changed;
            try { changed = await query.Where(value => ids.Contains(EF.Property<Guid>(value, key.Name))).ExecuteUpdateAsync(setters, ct); }
            finally { Integrity.AllowSetWrite = false; }
            if (changed != ids.Length) throw new DbUpdateConcurrencyException("The EF mutation affected-row count differs.");
            var after = await Data.Set<T>().AsNoTracking().Where(value => ids.Contains(EF.Property<Guid>(value, key.Name))).ToListAsync(ct);
            if (after.Count != ids.Length) throw new InvalidOperationException("The EF mutation changed an identity.");
            foreach (var value in after) Integrity.Capture(value);
            return changed;
        }
        catch { Integrity.Reject(); throw; }
    }
    private UnauthorizedAccessException Denied(string message)
    {
        Integrity.Reject();
        return new UnauthorizedAccessException(message);
    }
    internal async Task RequireWriterAsync(BrandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var registration = writer?.Find(context);
        if (registration is null || (writer!.Shared && !admitted)) throw Denied("Writer scope is unavailable.");
        var brand = Data.Set<Entities.Brand>().Where(value => value.Id == context.BrandId && value.BotId == context.BotId &&
            value.Active && value.PolicyVersion == registration.Revision);
        bool valid = write
            ? await AdmitAsync(() => brand.ExecuteUpdateAsync(setters => setters.SetProperty(value => value.PolicyVersion, value => value.PolicyVersion), ct)) == 1
            : await brand.AnyAsync(ct);
        if (!valid) throw Denied("Writer revision is unavailable.");
        markWriter?.Invoke(context, registration.Revision);
        admitted = true;
    }
    internal async Task RequireSharedWritersAsync(CancellationToken ct)
    {
        if (writer is not { Shared: true }) throw Denied("Shared writer scope is unavailable.");
        foreach (var registration in writer.Entries.OrderBy(value => value.Context.BrandId))
        {
            var query = Data.Set<Entities.Brand>().Where(value => value.Id == registration.Context.BrandId &&
                value.BotId == registration.Context.BotId && value.Active && value.PolicyVersion == registration.Revision);
            bool valid = write
                ? await AdmitAsync(() => query.ExecuteUpdateAsync(setters => setters.SetProperty(value => value.PolicyVersion, value => value.PolicyVersion), ct)) == 1
                : await query.AnyAsync(ct);
            if (!valid) throw Denied("Shared writer revision is unavailable.");
        }
        if (await Data.Set<Entities.Brand>().CountAsync(value => value.Active, ct) != writer.Entries.Length)
            throw Denied("Shared writer set is unavailable.");
        markShared?.Invoke();
        admitted = true;
    }
    internal async Task<Guid> RequireCustomerAsync(BrandContext context, long actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (write && writer?.Shared == true) throw Denied("Customer writes require a single-brand scope.");
        var members = Data.Set<Entities.User>().Where(value => value.BrandId == context.BrandId && value.TelegramId == actor &&
            value.BlockedAt == null && value.BannedAt == null);
        if (write && await AdmitAsync(() => members.ExecuteUpdateAsync(setters => setters.SetProperty(value => value.FlowRevision, value => value.FlowRevision), ct)) != 1)
            throw Denied("Customer scope is unavailable.");
        var member = await members.AsNoTracking().Select(value => new { value.Id }).SingleOrDefaultAsync(ct);
        if (member is null) throw Denied("Customer scope is unavailable.");
        if (writer is not null) await RequireWriterAsync(context, ct);
        else if (!await Data.Set<Entities.Brand>().AnyAsync(value => value.Id == context.BrandId && value.BotId == context.BotId && value.Active, ct))
            throw Denied("Brand scope is unavailable.");
        if (write && writer is null) throw Denied("Customer writes require a registered writer.");
        return member.Id;
    }
    internal async Task RequireAdminAsync(AuthenticatedAdmin actor, Guid? otherAdmin, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (write)
        {
            foreach (Guid id in new[] { actor.AdminId, otherAdmin ?? actor.AdminId }.Distinct().Order())
                if (await AdmitAsync(() => Data.Set<Entities.Admin>().Where(value => value.Id == id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.AuthVersion, value => value.AuthVersion), ct)) != 1)
                    throw Denied("Administrator scope is unavailable.");
        }
        byte[] hash = actor.TokenHash;
        bool valid = await (from admin in Data.Set<Entities.Admin>()
                            join session in Data.Set<AdminSession>() on admin.Id equals session.AdminId
                            where admin.Id == actor.AdminId && session.Id == actor.SessionId && session.TokenHash == hash &&
                                admin.DisabledAt == null && session.RevokedAt == null && admin.AuthVersion == actor.AuthVersion &&
                                session.AuthVersionAtIssue == actor.AuthVersion && session.ExpiresAt > EfValues.UtcNow()
                            select admin.Id).AnyAsync(ct);
        if (!valid) throw Denied("Administrator scope is unavailable.");
        if (writer is null) admitted = true;
    }
    internal async Task FlushAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Integrity.ThrowIfRejected();
        if (!write)
        {
            if (Data.ChangeTracker.HasChanges()) throw new InvalidOperationException("Read-only EF sessions cannot write.");
            return;
        }
        if (!admitted) throw Denied("A verified command scope is required.");
        await Data.SaveChangesAsync(ct);
        await Integrity.ValidateAsync(ct);
        ct.ThrowIfCancellationRequested();
    }
}

public sealed partial class SqliteSessionFactory
{
    internal Task<T> RunEfAsync<T>(bool write, WriterBinding? writer, Func<EfSession, CancellationToken, Task<T>> operation, CancellationToken ct) =>
        RunAsync(write, async (connection, transaction, token) =>
        {
            var options = new DbContextOptionsBuilder<SqliteAkantiDbContext>()
                .UseSqlite(connection, provider => provider.CommandTimeout(timeoutSeconds)).Options;
            await using var context = new SqliteAkantiDbContext(options);
            await context.Database.UseTransactionAsync(transaction, token);
            var session = new EfSession(context, write, writer);
            session.InitializeIntegrity();
            T result = await operation(session, token);
            await session.FlushAsync(token);
            return result;
        }, ct);
}

public sealed partial class DbSessionFactory
{
    internal async Task<T> RunEfAsync<T>(bool write, Func<EfSession, CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        await using var session = write ? await BeginAsync(ct) : await BeginReadOnlyAsync(ct);
        T result = await session.RunEfAsync(write, operation, ct);
        if (write) await session.CommitAsync(ct);
        return result;
    }
}

public sealed partial class DbSession
{
    internal async Task<T> RunEfAsync<T>(bool write, Func<EfSession, CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        EnterActive();
        try
        {
            var config = new DbContextOptionsBuilder<PostgresAkantiDbContext>()
                .UseNpgsql(connection, provider => provider.CommandTimeout(options.ClientTimeoutSeconds)
                    .ExecutionStrategy(dependencies => new NonRetryingExecutionStrategy(dependencies))).Options;
            await using var context = new PostgresAkantiDbContext(config);
            await context.Database.UseTransactionAsync(transaction, ct);
            var session = new EfSession(context, write, writer, MarkWriter, () => writerAdmitted = true);
            session.InitializeIntegrity();
            T result = await operation(session, ct);
            await session.FlushAsync(ct);
            return result;
        }
        catch (UnauthorizedAccessException) { State = DbSessionState.Aborted; throw; }
        catch (Exception error) { throw RecordFailure(error is DbUpdateException update ? update.InnerException ?? error : error, DbPhase.Executing, ct); }
        finally { Exit(); }
    }
}
