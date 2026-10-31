using Npgsql;
using NpgsqlTypes;
using Platform.Core;
using Platform.Persistence;

namespace Platform.UnitTests;

public sealed class DbStatementPolicyTests
{
    [Theory]
    [InlineData("COMMIT")]
    [InlineData("ROLLBACK")]
    [InlineData("BEGIN")]
    [InlineData("SELECT 1; COMMIT")]
    [InlineData("SELECT 1 -- hidden")]
    [InlineData("SELECT /* hidden */ 1")]
    [InlineData("SELECT $$text$$")]
    [InlineData("SELECT 'unclosed")]
    [InlineData("SELECT E'\\x'")]
    [InlineData("SELECT $0")]
    [InlineData("SELECT $2")]
    public void StatementsCannotOwnTheTransactionOrHideExtraCommands(string sql)
    {
        Assert.Throws<ArgumentException>(() => new DbStatement(sql));
    }

    [Fact]
    public void ParameterOrdinalsAndTypesAreExplicit()
    {
        _ = new DbStatement("SELECT $1::text, $1::text, ';--'::text", DbValue.Text("'; COMMIT; --"));
        Assert.Throws<ArgumentException>(() => new DbStatement("SELECT 1", DbValue.Text("unused")));
        Assert.Throws<ArgumentException>(() => new DbStatement("SELECT $2", DbValue.Text("unused"), DbValue.Text("missing first")));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DbStatement("SELECT $1", default(DbValue)));
        Assert.Throws<ArgumentOutOfRangeException>(() => DbValue.Null(NpgsqlDbType.Jsonb));
        Assert.Throws<ArgumentException>(() => DbValue.Money(new Money(1, Currency.Usdt), Currency.Usd));
        Assert.Throws<ArgumentException>(() => DbValue.Utc(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Unspecified)));
        Assert.Throws<ArgumentException>(() => DbValue.Utc(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)));
    }

    [Theory]
    [InlineData("40001", DbFailureKind.Concurrency, DbRecovery.RetryWholeTransactionAfterRollback)]
    [InlineData("40P01", DbFailureKind.Concurrency, DbRecovery.RetryWholeTransactionAfterRollback)]
    [InlineData("55P03", DbFailureKind.Busy, DbRecovery.RetryWholeTransactionAfterRollback)]
    [InlineData("23505", DbFailureKind.Constraint, DbRecovery.Reject)]
    [InlineData("23514", DbFailureKind.Constraint, DbRecovery.Reject)]
    [InlineData("40003", DbFailureKind.OutcomeUnknown, DbRecovery.ReconcileBeforeRetry)]
    [InlineData("08007", DbFailureKind.OutcomeUnknown, DbRecovery.ReconcileBeforeRetry)]
    public void RecoveryUsesSqlStateWithoutTreatingEveryErrorAsTransient(string code, DbFailureKind kind, DbRecovery recovery)
    {
        var postgres = new PostgresException("private synthetic detail", "ERROR", "ERROR", code);
        DbFailure failure = DbFailurePolicy.Classify(postgres, DbPhase.Committing);
        Assert.Equal(kind, failure.Kind);
        Assert.Equal(recovery, failure.Recovery);
        var safe = new DbOperationException(failure);
        Assert.Null(safe.InnerException);
        Assert.DoesNotContain("private synthetic detail", safe.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TransportOrCancellationDuringCommitAlwaysRequiresReconciliation()
    {
        foreach (Exception error in new Exception[] { new IOException("private synthetic detail"), new OperationCanceledException(), new TimeoutException() })
        {
            Assert.Equal(DbRecovery.ReconcileBeforeRetry, DbFailurePolicy.Classify(error, DbPhase.Committing).Recovery);
        }
        Assert.Equal(DbRecovery.ReconnectBeforeWork, DbFailurePolicy.Classify(new IOException(), DbPhase.Opening).Recovery);
        Assert.Equal(DbRecovery.Cancel, DbFailurePolicy.Classify(new OperationCanceledException(), DbPhase.Executing).Recovery);
        Assert.Equal(DbRecovery.Reject, DbFailurePolicy.Classify(new PostgresException("private detail", "ERROR", "ERROR", "42601"), DbPhase.Executing).Recovery);
        Assert.Equal(DbRecovery.Reject, DbFailurePolicy.Classify(new PostgresException("private detail", "ERROR", "ERROR", "28P01"), DbPhase.Opening).Recovery);
        var malformed = DbFailurePolicy.Classify(new PostgresException("private detail", "ERROR", "ERROR", "private-detail"), DbPhase.Committing);
        Assert.Null(malformed.SqlState);
        Assert.Equal(DbRecovery.ReconcileBeforeRetry, malformed.Recovery);
        Assert.DoesNotContain("private-detail", new DbOperationException(malformed).ToString(), StringComparison.Ordinal);
    }
}
