using Npgsql;

namespace Platform.Persistence;

public enum DbPhase { Opening, Executing, Committing, RollingBack }
public enum DbFailureKind { Concurrency, Busy, Constraint, Connection, Canceled, OutcomeUnknown, LeaseLost, InvalidData, Unexpected }
public enum DbRecovery { Reject, RetryWholeTransactionAfterRollback, ReconnectBeforeWork, ReconcileBeforeRetry, StopLeaseOwner, Cancel }
public sealed record DbFailure
{
    public DbFailureKind Kind { get; }
    public DbRecovery Recovery { get; }
    public string? SqlState { get; }
    public DbFailure(DbFailureKind kind, DbRecovery recovery, string? sqlState = null)
    {
        Kind = kind; Recovery = recovery; SqlState = NormalizeState(sqlState);
    }
    internal static string? NormalizeState(string? code) => code is { Length: 5 } &&
        code.All(c => char.IsAsciiDigit(c) || c is >= 'A' and <= 'Z') ? code : null;
}

public sealed class DbOperationException : Exception
{
    public DbFailure Failure { get; }
    // Deliberately no original exception/SQL/parameter/server detail in Message, Data or InnerException.
    public DbOperationException(DbFailure failure) : base("Database operation failed: " + failure.Kind +
        (failure.SqlState is null ? "." : " (SQLSTATE " + failure.SqlState + ").")) => Failure = failure;
}

public static class DbFailurePolicy
{
    public static DbFailure Classify(Exception error, DbPhase phase)
    {
        string? code = DbFailure.NormalizeState((error as PostgresException)?.SqlState);
        bool knownCommitRejection = code is "40001" or "40P01" or "55P03" || code?.StartsWith("23", StringComparison.Ordinal) == true;
        if (code is "40003" or "08007" || (phase == DbPhase.Committing && !knownCommitRejection))
            return new(DbFailureKind.OutcomeUnknown, DbRecovery.ReconcileBeforeRetry, code);
        if (code is "40001" or "40P01") return new(DbFailureKind.Concurrency, DbRecovery.RetryWholeTransactionAfterRollback, code);
        if (code == "55P03") return new(DbFailureKind.Busy, DbRecovery.RetryWholeTransactionAfterRollback, code);
        if (code?.StartsWith("23", StringComparison.Ordinal) == true) return new(DbFailureKind.Constraint, DbRecovery.Reject, code);
        if (error is OperationCanceledException || code == "57014") return new(DbFailureKind.Canceled, DbRecovery.Cancel, code);
        if (code is not null)
        {
            if (code.StartsWith("08", StringComparison.Ordinal) || code is "57P01" or "57P02" or "57P03")
                return new(DbFailureKind.Connection, phase == DbPhase.Opening ? DbRecovery.ReconnectBeforeWork : DbRecovery.RetryWholeTransactionAfterRollback, code);
            return new(code.StartsWith("22", StringComparison.Ordinal) ? DbFailureKind.InvalidData : DbFailureKind.Unexpected, DbRecovery.Reject, code);
        }
        if (error is PostgresException) return new(DbFailureKind.Unexpected, DbRecovery.Reject);
        if (error is NpgsqlException or IOException or TimeoutException)
            return new(DbFailureKind.Connection, phase == DbPhase.Opening ? DbRecovery.ReconnectBeforeWork : DbRecovery.RetryWholeTransactionAfterRollback, code);
        if (error is FormatException or OverflowException or ArgumentException) return new(DbFailureKind.InvalidData, DbRecovery.Reject, code);
        return new(DbFailureKind.Unexpected, DbRecovery.Reject, code);
    }
}
