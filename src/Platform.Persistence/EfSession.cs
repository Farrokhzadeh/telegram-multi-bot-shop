using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using Platform.Persistence.Configurations;
using Platform.Persistence.Entities;
using Platform.Security;

namespace Platform.Persistence;

internal sealed partial class EfSession(AkantiDbContext data, bool write, WriterBinding? writer, Action<BrandContext, long>? markWriter = null, Action? markShared = null, EfIntegrityScope? changes = null, bool nativeWriterBoundary = false)
{
    private bool admitted, sharedAdmitted;
    internal void InitializeIntegrity() => _ = Integrity;
    internal AkantiDbContext Data { get; } = data;
    private EfIntegrity Integrity => Data.Integrity ??= new EfIntegrity(Data, () => write && admitted, changes);
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
            var prior = before.ToDictionary(value => (Guid)Data.Entry(value).Property(key.Name).CurrentValue!);
            Guid[] ids = prior.Keys.ToArray();
            if (ids.Length == 0) return 0;
            foreach (var value in before) Integrity.Capture(value);
            Integrity.AllowSetWrite = true;
            int changed;
            try { changed = await query.Where(value => ids.Contains(EF.Property<Guid>(value, key.Name))).ExecuteUpdateAsync(setters, ct); }
            finally { Integrity.AllowSetWrite = false; }
            if (changed != ids.Length) throw new DbUpdateConcurrencyException("The EF mutation affected-row count differs.");
            var after = await Data.Set<T>().AsNoTracking().Where(value => ids.Contains(EF.Property<Guid>(value, key.Name))).ToListAsync(ct);
            if (after.Count != ids.Length) throw new InvalidOperationException("The EF mutation changed an identity.");
            foreach (var value in after)
            {
                Integrity.RequireMutation(prior[(Guid)Data.Entry(value).Property(key.Name).CurrentValue!], value);
                Integrity.Capture(value);
            }
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
        if (registration is null || (writer!.Shared && !sharedAdmitted)) throw Denied("Writer scope is unavailable.");
        var brand = Data.Set<Entities.Brand>().Where(value => value.Id == context.BrandId && value.BotId == context.BotId &&
            value.Active && value.PolicyVersion == registration.Revision);
        bool valid = write
            ? await LockRowsAsync(brand.Select(value => value.Id), EfRowLock.NoKeyUpdate, ct) == 1
            : await brand.AnyAsync(ct);
        if (!valid) throw Denied("Writer revision is unavailable.");
        markWriter?.Invoke(context, registration.Revision);
        admitted = true;
    }
    internal async Task RequireSharedWritersAsync(CancellationToken ct, bool lockRows = false)
    {
        if (writer is not { Shared: true }) throw Denied("Shared writer scope is unavailable.");
        var brands = await Data.Set<Entities.Brand>().OrderBy(value => value.Id).Select(value => value.Id).Take(1001).ToArrayAsync(ct);
        if (brands.Length > 1000) throw Denied("The shared brand set exceeds its bound.");
        if (write || lockRows) foreach (var brand in brands) await LockBrandAsync(brand, ct);
        var enabled = await Data.Set<Entities.Brand>().AsNoTracking().Where(value => value.Active).Select(value => new { value.Id, value.BotId, value.PolicyVersion }).Take(1001).ToArrayAsync(ct);
        if (enabled.Length != writer.Entries.Length || writer.Entries.Any(value => !enabled.Any(brand => brand.Id == value.Context.BrandId && brand.BotId == value.Context.BotId && brand.PolicyVersion == value.Revision)))
            throw Denied("Shared writer set is unavailable.");
        markShared?.Invoke();
        sharedAdmitted = admitted = true;
    }
    internal async Task<Guid> RequireCustomerAsync(BrandContext context, long actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (write && writer?.Shared == true) throw Denied("Customer writes require a single-brand scope.");
        var members = Data.Set<Entities.User>().Where(value => value.BrandId == context.BrandId && value.TelegramId == actor &&
            value.BlockedAt == null && value.BannedAt == null);
        if (write && await LockRowsAsync(members.Select(value => value.Id), EfRowLock.Update, ct) != 1)
            throw Denied("Customer scope is unavailable.");
        var member = await members.AsNoTracking().Select(value => new { value.Id }).SingleOrDefaultAsync(ct);
        if (member is null) throw Denied("Customer scope is unavailable.");
        if (writer is not null) await RequireWriterAsync(context, ct);
        else if (!await Data.Set<Entities.Brand>().AnyAsync(value => value.Id == context.BrandId && value.BotId == context.BotId && value.Active, ct))
            throw Denied("Brand scope is unavailable.");
        if (write && writer is null) throw Denied("Customer writes require a registered writer.");
        return member.Id;
    }
    internal async Task RequireAdminAsync(AuthenticatedAdmin actor, Guid? otherAdmin, CancellationToken ct, bool lockRows = false)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (write || lockRows)
        {
            foreach (Guid id in new[] { actor.AdminId, otherAdmin ?? actor.AdminId }.Distinct().Order())
                if (await LockAdminAsync(id, ct) != 1 && id == actor.AdminId)
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
        if (writer is null || nativeWriterBoundary) admitted = true;
    }
    internal async Task<Entities.Admin> RequirePasswordAsync(string login, ReadOnlyMemory<char> password, CancellationToken ct)
    {
        if (!write || (writer is not null && !nativeWriterBoundary)) throw Denied("Administrator login requires a writable control session.");
        await LockRowsAsync(Data.Set<Entities.Admin>().Where(value => value.Login == login).Select(value => value.Id), EfRowLock.Update, ct);
        var account = await Data.Set<Entities.Admin>().AsNoTracking().SingleOrDefaultAsync(value => value.Login == login, ct);
        bool valid = AdminAuth.VerifyPassword(password.Span, account?.PasswordVerifier);
        if (!valid || account is null || account.DisabledAt is not null || account.AuthenticationMode != "password") RejectAuthentication();
        admitted = true;
        return account;
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    internal void RejectAuthentication()
    {
        Integrity.Reject();
        throw new AdminAuthenticationException();
    }
    internal async Task FlushAsync(CancellationToken ct, bool validate = true)
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
        if (validate) await Integrity.ValidateAsync(ct);
        ct.ThrowIfCancellationRequested();
    }
}

public sealed partial class SqliteSessionFactory
{
    internal static Task<T> ReadEfAsync<T>(Microsoft.Data.Sqlite.SqliteTransaction transaction, Func<EfSession, CancellationToken, Task<T>> operation, CancellationToken ct) =>
        StepEfAsync(transaction, false, operation, ct);
    internal Task<T> RunEfAsync<T>(bool write, WriterBinding? writer, Func<EfSession, CancellationToken, Task<T>> operation, CancellationToken ct) =>
        RunAsync(write, (_, transaction, token) => StepEfAsync(transaction, write, operation, token, writer, true), ct);
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
    private EfIntegrityScope? efChanges;
    internal Task<T> RunEfAsync<T>(bool write, Func<EfSession, CancellationToken, Task<T>> operation, CancellationToken ct) =>
        RunEfCoreAsync(write, false, operation, ct);
    internal Task<T> RunEfStepAsync<T>(bool write, Func<EfSession, CancellationToken, Task<T>> operation, CancellationToken ct) =>
        RunEfCoreAsync(write, true, operation, ct);
    private PostgresAkantiDbContext EfContext() => new(new DbContextOptionsBuilder<PostgresAkantiDbContext>()
        .UseNpgsql(connection, provider => provider.CommandTimeout(options.ClientTimeoutSeconds)
            .ExecutionStrategy(dependencies => new NonRetryingExecutionStrategy(dependencies))).Options);
    private async Task<T> RunEfCoreAsync<T>(bool write, bool defer, Func<EfSession, CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        EnterActive();
        try
        {
            await using var context = EfContext();
            await context.Database.UseTransactionAsync(transaction, ct);
            var session = new EfSession(context, write, writer, MarkWriter, () => writerAdmitted = true, efChanges ??= new(), true);
            session.RestoreSharedAdmission(writerAdmitted);
            session.InitializeIntegrity();
            T result = await operation(session, ct);
            await session.FlushAsync(ct, !defer);
            return result;
        }
        catch (EfApplicationException error) { LastFailure = new(DbFailureKind.InvalidData, DbRecovery.Reject); State = DbSessionState.Aborted; if (error.Argument) throw new ArgumentException(error.Message); throw new InvalidOperationException(error.Message); }
        catch (AdminAuthenticationException) { LastFailure = new(DbFailureKind.InvalidData, DbRecovery.Reject); State = DbSessionState.Aborted; throw; }
        catch (UnauthorizedAccessException) { State = DbSessionState.Aborted; throw; }
        catch (Exception error) { throw RecordFailure(error is DbUpdateException update ? update.InnerException ?? error : error, DbPhase.Executing, ct); }
        finally { Exit(); }
    }
    private async Task ValidateEfAsync(CancellationToken ct)
    {
        if (efChanges is null) return;
        if (efChanges.Rejected) throw new InvalidOperationException("The EF command was rejected and cannot commit.");
        if (efChanges.Empty) return;
        await using var context = EfContext();
        await context.Database.UseTransactionAsync(transaction, ct);
        var integrity = new EfIntegrity(context, () => false, efChanges);
        context.Integrity = integrity;
        await integrity.ValidateAsync(ct);
    }
}
