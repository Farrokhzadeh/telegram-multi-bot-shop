using Microsoft.Data.Sqlite;
using Platform.Core;

namespace Platform.Persistence;

internal static class SqliteWallet
{
    internal static Task<(Money Balance, long Version)> ReadAsync(SqliteTransaction tx, Guid member, CancellationToken ct) =>
        SqliteSessionFactory.StepEfAsync(tx, false, (data, token) => WalletOperations.ReadAsync(data, member, token), ct);
    internal static Task<Guid> PostAsync(SqliteTransaction tx, Guid member, (Money Balance, long Version) wallet, Money change, Guid origin, bool topup, string now, CancellationToken ct) =>
        SqliteSessionFactory.StepEfAsync(tx, true, (data, token) => WalletOperations.PostAsync(data, member, change, wallet.Version,
            new Entities.WalletEntry { Id = Guid.CreateVersion7(), MovementKind = topup ? "wallet_topup" : "purchase_debit", IdempotencyKey = topup ? $"payment-credit:{origin:D}" : $"checkout:{origin:D}", OrderId = topup ? null : origin, PaymentId = topup ? origin : null, Reason = topup ? "verified_original_invoice" : "customer_request" }, token, DateTime.ParseExact(now, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal)), ct);

    internal static SqliteCommand Command(SqliteTransaction tx, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = tx.Connection!.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
}
