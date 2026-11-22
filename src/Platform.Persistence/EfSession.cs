using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Configurations;
using Platform.Persistence.Entities;
using Platform.Security;

namespace Platform.Persistence;

internal sealed class EfSession(AkantiDbContext data, bool write, WriterBinding? writer, Action<BrandContext, long>? markWriter = null, Action? markShared = null)
{
    private bool admitted;
    internal AkantiDbContext Data { get; } = data;
    internal async Task RequireWriterAsync(BrandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var registration = writer?.Find(context);
        if (registration is null || (writer!.Shared && !admitted)) throw new UnauthorizedAccessException("Writer scope is unavailable.");
        var brand = Data.Set<Entities.Brand>().Where(value => value.Id == context.BrandId && value.BotId == context.BotId &&
            value.Active && value.PolicyVersion == registration.Revision);
        bool valid = write
            ? await brand.ExecuteUpdateAsync(setters => setters.SetProperty(value => value.PolicyVersion, value => value.PolicyVersion), ct) == 1
            : await brand.AnyAsync(ct);
        if (!valid) throw new UnauthorizedAccessException("Writer revision is unavailable.");
        markWriter?.Invoke(context, registration.Revision);
        admitted = true;
    }
    internal async Task RequireSharedWritersAsync(CancellationToken ct)
    {
        if (writer is not { Shared: true }) throw new UnauthorizedAccessException("Shared writer scope is unavailable.");
        foreach (var registration in writer.Entries.OrderBy(value => value.Context.BrandId))
        {
            var query = Data.Set<Entities.Brand>().Where(value => value.Id == registration.Context.BrandId &&
                value.BotId == registration.Context.BotId && value.Active && value.PolicyVersion == registration.Revision);
            bool valid = write
                ? await query.ExecuteUpdateAsync(setters => setters.SetProperty(value => value.PolicyVersion, value => value.PolicyVersion), ct) == 1
                : await query.AnyAsync(ct);
            if (!valid) throw new UnauthorizedAccessException("Shared writer revision is unavailable.");
        }
        if (await Data.Set<Entities.Brand>().CountAsync(value => value.Active, ct) != writer.Entries.Length)
            throw new UnauthorizedAccessException("Shared writer set is unavailable.");
        markShared?.Invoke();
        admitted = true;
    }
    internal async Task<Guid> RequireCustomerAsync(BrandContext context, long actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (write && writer?.Shared == true) throw new UnauthorizedAccessException("Customer writes require a single-brand scope.");
        var members = Data.Set<Entities.User>().Where(value => value.BrandId == context.BrandId && value.TelegramId == actor &&
            value.BlockedAt == null && value.BannedAt == null);
        if (write && await members.ExecuteUpdateAsync(setters => setters.SetProperty(value => value.FlowRevision, value => value.FlowRevision), ct) != 1)
            throw new UnauthorizedAccessException("Customer scope is unavailable.");
        var member = await members.AsNoTracking().Select(value => new { value.Id }).SingleOrDefaultAsync(ct);
        if (member is null) throw new UnauthorizedAccessException("Customer scope is unavailable.");
        if (writer is not null) await RequireWriterAsync(context, ct);
        else if (!await Data.Set<Entities.Brand>().AnyAsync(value => value.Id == context.BrandId && value.BotId == context.BotId && value.Active, ct))
            throw new UnauthorizedAccessException("Brand scope is unavailable.");
        if (write && writer is null) throw new UnauthorizedAccessException("Customer writes require a registered writer.");
        return member.Id;
    }
    internal async Task RequireAdminAsync(AuthenticatedAdmin actor, Guid? otherAdmin, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (write)
        {
            foreach (Guid id in new[] { actor.AdminId, otherAdmin ?? actor.AdminId }.Distinct().Order())
                if (await Data.Set<Entities.Admin>().Where(value => value.Id == id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.AuthVersion, value => value.AuthVersion), ct) != 1)
                    throw new UnauthorizedAccessException("Administrator scope is unavailable.");
        }
        byte[] hash = actor.TokenHash;
        bool valid = await (from admin in Data.Set<Entities.Admin>()
                            join session in Data.Set<AdminSession>() on admin.Id equals session.AdminId
                            where admin.Id == actor.AdminId && session.Id == actor.SessionId && session.TokenHash == hash &&
                                admin.DisabledAt == null && session.RevokedAt == null && admin.AuthVersion == actor.AuthVersion &&
                                session.AuthVersionAtIssue == actor.AuthVersion && session.ExpiresAt > EfValues.UtcNow()
                            select admin.Id).AnyAsync(ct);
        if (!valid) throw new UnauthorizedAccessException("Administrator scope is unavailable.");
        if (writer is null) admitted = true;
    }
    internal async Task FlushAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!write)
        {
            if (Data.ChangeTracker.HasChanges()) throw new InvalidOperationException("Read-only EF sessions cannot write.");
            return;
        }
        if (!admitted) throw new UnauthorizedAccessException("A verified command scope is required.");
        await Data.SaveChangesAsync(ct);
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
                .UseNpgsql(connection, provider => provider.CommandTimeout(options.ClientTimeoutSeconds)).Options;
            await using var context = new PostgresAkantiDbContext(config);
            await context.Database.UseTransactionAsync(transaction, ct);
            var session = new EfSession(context, write, writer, MarkWriter, () => writerAdmitted = true);
            T result = await operation(session, ct);
            await session.FlushAsync(ct);
            return result;
        }
        catch (UnauthorizedAccessException) { State = DbSessionState.Aborted; throw; }
        catch (Exception error) { throw RecordFailure(error is DbUpdateException update ? update.InnerException ?? error : error, DbPhase.Executing, ct); }
        finally { Exit(); }
    }
}
