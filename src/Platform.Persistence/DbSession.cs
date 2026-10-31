using System.Data;
using System.Globalization;
using Npgsql;

namespace Platform.Persistence;

public enum DbSessionState { Active, Aborted, Committed, RolledBack, OutcomeUnknown }

public sealed record DbSessionOptions
{
    public IsolationLevel Isolation { get; init; } = IsolationLevel.ReadCommitted;
    public TimeSpan StatementTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan RollbackTimeout { get; init; } = TimeSpan.FromSeconds(5);
    internal int ClientTimeoutSeconds => checked((int)Math.Ceiling(StatementTimeout.TotalSeconds) + 5);

    internal void Validate()
    {
        if (Isolation is not (IsolationLevel.ReadCommitted or IsolationLevel.RepeatableRead or IsolationLevel.Serializable))
            throw new ArgumentOutOfRangeException(nameof(Isolation));
        foreach (TimeSpan value in new[] { StatementTimeout, LockTimeout, RollbackTimeout })
        {
            if (value <= TimeSpan.Zero || value.TotalMilliseconds > int.MaxValue || value.Ticks % TimeSpan.TicksPerMillisecond != 0)
                throw new ArgumentOutOfRangeException(nameof(StatementTimeout), "Timeouts require positive whole milliseconds within PostgreSQL's supported range.");
        }
        if (LockTimeout > StatementTimeout) throw new ArgumentException("Lock timeout cannot exceed statement timeout.");
    }
}

public sealed class DbSessionFactory
{
    private readonly NpgsqlDataSource data;
    private readonly DbSessionOptions options;

    public DbSessionFactory(NpgsqlDataSource data, DbSessionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        this.options = options ?? new DbSessionOptions();
        this.options.Validate();
        var connection = new NpgsqlConnectionStringBuilder(data.ConnectionString);
        if (connection.IncludeErrorDetail || connection.Multiplexing)
            throw new ArgumentException("Sessions require redacted server errors and a dedicated transaction connection.");
        this.data = data; // Caller owns the shared data source/pool.
    }

    public async Task<DbSession> BeginAsync(CancellationToken cancellationToken = default)
    {
        if (System.Transactions.Transaction.Current is not null)
            throw new InvalidOperationException("Ambient transactions are not supported.");
        cancellationToken.ThrowIfCancellationRequested();
        NpgsqlConnection? connection = null;
        DbSession? session = null;
        try
        {
            connection = await data.OpenConnectionAsync(cancellationToken);
            var transaction = await connection.BeginTransactionAsync(options.Isolation, cancellationToken);
            session = new DbSession(connection, transaction, options);
            await session.ExecuteAsync(new DbStatement(
                "SELECT set_config('statement_timeout',$1,true),set_config('lock_timeout',$2,true),set_config('TimeZone','UTC',true)",
                DbValue.Text(((int)options.StatementTimeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + "ms"),
                DbValue.Text(((int)options.LockTimeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + "ms")), cancellationToken);
            return session;
        }
        catch (Exception error)
        {
            if (session is not null) await session.DisposeAsync();
            else if (connection is not null)
            {
                try { await connection.DisposeAsync(); } catch (Exception) { /* Preserve the original safe opening failure. */ }
            }
            if (error is DbOperationException) throw;
            DbFailure failure = DbFailurePolicy.Classify(error, DbPhase.Opening);
            if (failure.Kind == DbFailureKind.Canceled) throw new OperationCanceledException("Database opening canceled.", cancellationToken);
            throw new DbOperationException(failure);
        }
    }
}

public sealed class DbSession : IAsyncDisposable
{
    private readonly NpgsqlConnection connection;
    private readonly NpgsqlTransaction transaction;
    private readonly DbSessionOptions options;
    private int busy;
    private bool disposed;
    public DbSessionState State { get; private set; } = DbSessionState.Active;
    public DbFailure? LastFailure { get; private set; }
    public DbFailure? CleanupFailure { get; private set; }

    internal DbSession(NpgsqlConnection connection, NpgsqlTransaction transaction, DbSessionOptions options)
    {
        this.connection = connection;
        this.transaction = transaction;
        this.options = options;
    }

    public Task<int> ExecuteAsync(DbStatement statement, CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(statement, false, cancellationToken);

    // The repository statement MUST compare the durable lease owner, generation and expiry.
    // This enforces the affected-row fence; it neither acquires a lease nor permits remote resubmission.
    public Task<int> ExecuteLeaseFencedAsync(DbStatement statement, CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(statement, true, cancellationToken);

    private async Task<int> ExecuteCoreAsync(DbStatement statement, bool leaseFenced, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statement);
        EnterActive();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var command = statement.CreateCommand(connection, transaction, options.ClientTimeoutSeconds);
            int affected = await command.ExecuteNonQueryAsync(cancellationToken);
            if (leaseFenced && affected != 1)
                throw new DbOperationException(affected == 0 ? new(DbFailureKind.LeaseLost, DbRecovery.StopLeaseOwner) : new(DbFailureKind.Unexpected, DbRecovery.Reject));
            return affected;
        }
        catch (Exception error) { throw RecordFailure(error, DbPhase.Executing, cancellationToken); }
        finally { Exit(); }
    }

    public async Task<T> QuerySingleAsync<T>(DbStatement statement, Func<NpgsqlDataReader, T> project,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(project);
        EnterActive();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var command = statement.CreateCommand(connection, transaction, options.ClientTimeoutSeconds);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Query requires exactly one row.");
            T result = project(reader);
            if (await reader.ReadAsync(cancellationToken) || await reader.NextResultAsync(cancellationToken))
                throw new InvalidOperationException("Query requires exactly one result row.");
            return result;
        }
        catch (Exception error) { throw RecordFailure(error, DbPhase.Executing, cancellationToken); }
        finally { Exit(); }
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        EnterActive();
        bool dispatched = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            dispatched = true; // From here, cancellation/transport loss is conservatively an unknown commit.
            await transaction.CommitAsync(cancellationToken);
            State = DbSessionState.Committed;
        }
        catch (Exception error)
        {
            throw RecordFailure(error, dispatched ? DbPhase.Committing : DbPhase.Executing, cancellationToken);
        }
        finally { Exit(); }
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        Enter();
        try
        {
            if (State is not (DbSessionState.Active or DbSessionState.Aborted))
                throw new InvalidOperationException("Only a known uncommitted session can roll back.");
            try
            {
                await transaction.RollbackAsync(cancellationToken);
                State = DbSessionState.RolledBack;
            }
            catch (Exception error) { throw RecordFailure(error, DbPhase.RollingBack, cancellationToken); }
        }
        finally { Exit(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        Enter();
        disposed = true;
        try
        {
            if (State is DbSessionState.Active or DbSessionState.Aborted)
            {
                using var cleanup = new CancellationTokenSource(options.RollbackTimeout);
                try
                {
                    await transaction.RollbackAsync(cleanup.Token);
                    State = DbSessionState.RolledBack;
                }
                catch (Exception error) { CleanupFailure = DbFailurePolicy.Classify(error, DbPhase.RollingBack); }
            }
            if (State == DbSessionState.OutcomeUnknown)
            {
                // Clear only this source's pool; the uncertain connector is discarded on return.
                try { NpgsqlConnection.ClearPool(connection); }
                catch (Exception error) { CleanupFailure ??= DbFailurePolicy.Classify(error, DbPhase.RollingBack); }
            }
            try { await transaction.DisposeAsync(); }
            catch (Exception error) { CleanupFailure ??= DbFailurePolicy.Classify(error, DbPhase.RollingBack); }
            try { await connection.DisposeAsync(); }
            catch (Exception error) { CleanupFailure ??= DbFailurePolicy.Classify(error, DbPhase.RollingBack); }
            // Cleanup failure never turns an acknowledged commit into a retryable operation.
        }
        finally { Exit(); }
    }

    private Exception RecordFailure(Exception error, DbPhase phase, CancellationToken cancellationToken)
    {
        DbFailure failure = error is DbOperationException safe ? safe.Failure : DbFailurePolicy.Classify(error, phase);
        LastFailure = failure;
        State = failure.Recovery == DbRecovery.ReconcileBeforeRetry ? DbSessionState.OutcomeUnknown :
            phase == DbPhase.Committing ? DbSessionState.RolledBack : DbSessionState.Aborted;
        return failure.Kind == DbFailureKind.Canceled
            ? new OperationCanceledException("Database transaction canceled before commit.", cancellationToken)
            : new DbOperationException(failure);
    }

    private void EnterActive()
    {
        Enter();
        if (State != DbSessionState.Active) { Exit(); throw new InvalidOperationException("Database session is no longer active."); }
    }

    private void Enter()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) throw new InvalidOperationException("Database sessions do not allow concurrent operations.");
    }
    private void Exit() => Interlocked.Exchange(ref busy, 0);
}
