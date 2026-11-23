using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Platform.Persistence;

internal sealed class EfMutationInterceptor : DbCommandInterceptor
{
    internal static readonly EfMutationInterceptor Instance = new();
    private static void Verify(CommandEventData command)
    {
        if (command.Context is not AkantiDbContext { Integrity: { } integrity }) return;
        integrity.ThrowIfRejected();
        bool invalid = command.CommandSource switch
        {
            CommandSource.LinqQuery or CommandSource.SaveChanges or CommandSource.ValueGenerator => false,
            CommandSource.ExecuteUpdate => !integrity.AllowSetWrite,
            _ => true
        };
        if (!invalid) return;
        integrity.Reject();
        throw new InvalidOperationException("EF command sessions require an affected-scope mutation adapter.");
    }
    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Verify(eventData);
        return result;
    }
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Verify(eventData);
        return ValueTask.FromResult(result);
    }
    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Verify(eventData);
        return result;
    }
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Verify(eventData);
        return ValueTask.FromResult(result);
    }
    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Verify(eventData);
        return result;
    }
    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Verify(eventData);
        return ValueTask.FromResult(result);
    }
}
