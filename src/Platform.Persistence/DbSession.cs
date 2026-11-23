using System.Data;
using System.Globalization;
using Npgsql;
using Platform.Security;

namespace Platform.Persistence;

public enum DbSessionState { Active, Aborted, Committed, RolledBack, OutcomeUnknown }

public sealed record DbSessionOptions
{
    public IsolationLevel Isolation { get; init; } = IsolationLevel.ReadCommitted;
    public bool ReadOnly { get; init; }
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

public sealed partial class DbSessionFactory
{
    private readonly NpgsqlDataSource data;
    private readonly DbSessionOptions options;
    private WriterBinding? writer;

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

    public bool IsWriterScoped => writer is not null;
    public long? ExpectedWriterRevision(BrandContext context)
    {
        if (writer is null) return null;
        return writer.Find(context)?.Revision ?? throw new UnauthorizedAccessException("Writer registration is unavailable.");
    }
    public DbSessionFactory ForBrandWriter(BrandContext context, long expectedRevision) => Bind(new WriterBinding([new(context, expectedRevision)], false));
    public DbSessionFactory ForSharedWriters(IEnumerable<BrandWriterRegistration> registrations) => Bind(new WriterBinding(registrations, true));
    private DbSessionFactory Bind(WriterBinding binding)
    {
        if (options.ReadOnly || writer is not null) throw new InvalidOperationException("Writer factories cannot be read-only or rebound.");
        return new DbSessionFactory(data, options) { writer = binding };
    }
    public Task<DbSession> BeginReadOnlyAsync(CancellationToken cancellationToken = default) => new DbSessionFactory(data, options with { ReadOnly = true, Isolation = IsolationLevel.RepeatableRead }).BeginAsync(cancellationToken);
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
            if (options.ReadOnly)
            {
                await using var control = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction);
                await control.ExecuteNonQueryAsync(cancellationToken);
            }
            session = new DbSession(connection, transaction, options);
            session.BindWriter(writer);
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

public sealed partial class DbSession : IAsyncDisposable
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

    public async Task<SchemaReceipt> InspectSchemaAsync(CancellationToken cancellationToken = default)
    {
        EnterActive();
        try { return await SchemaRunner.InspectReadOnlyAsync(connection, transaction, cancellationToken); }
        catch (Exception error) { throw RecordFailure(error, DbPhase.Executing, cancellationToken); }
        finally { Exit(); }
    }

    public bool MatchesEndpoint(string host, int port, string database, string username)
    {
        var endpoint = new NpgsqlConnectionStringBuilder(connection.ConnectionString);
        return endpoint.Host == host && endpoint.Port == port && endpoint.Database == database && endpoint.Username == username;
    }

    public async Task<T> QuerySingleAsync<T>(DbStatement statement, Func<DbRow, T> project,
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
            var row = new DbRow(reader);
            T result;
            try { result = project(row); }
            finally { row.Invalidate(); }
            if (await reader.ReadAsync(cancellationToken) || await reader.NextResultAsync(cancellationToken))
                throw new InvalidOperationException("Query requires exactly one result row.");
            return result;
        }
        catch (Exception error) { throw RecordFailure(error, DbPhase.Executing, cancellationToken); }
        finally { Exit(); }
    }

    public async Task<IReadOnlyList<T>> QueryManyAsync<T>(DbStatement statement, Func<DbRow, T> project, int maximumRows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(project);
        if (maximumRows is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maximumRows));
        EnterActive();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var command = statement.CreateCommand(connection, transaction, options.ClientTimeoutSeconds);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var results = new List<T>();
            while (await reader.ReadAsync(cancellationToken))
            {
                if (results.Count == maximumRows) throw new InvalidOperationException("Query row limit exceeded.");
                var row = new DbRow(reader);
                try { results.Add(project(row)); }
                finally { row.Invalidate(); }
            }
            if (await reader.NextResultAsync(cancellationToken)) throw new InvalidOperationException("Only one result set is allowed.");
            return results.AsReadOnly();
        }
        catch (Exception error) { throw RecordFailure(error, DbPhase.Executing, cancellationToken); }
        finally { Exit(); }
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        RequireWriterCommit();
        EnterActive();
        bool dispatched = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ValidateEfAsync(cancellationToken);
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

    internal void RejectApplication()
    {
        EnterActive();
        try
        {
            LastFailure = new DbFailure(DbFailureKind.InvalidData, DbRecovery.Reject);
            State = DbSessionState.Aborted;
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
