using Microsoft.Data.Sqlite;
using Platform.Core;

namespace Platform.Persistence;

internal static class SqliteWallet
{
    internal static async Task<(Money Balance, long Version)> ReadAsync(SqliteTransaction tx, Guid member, CancellationToken ct)
    {
        using var command = Command(tx, """
            SELECT w.balance,w.version,e.balance_after
            FROM wallets w LEFT JOIN wallet_entries e ON e.user_id=w.user_id AND e.posting_version=w.version AND e.is_posting
            WHERE w.user_id=$member
            """, ("$member", member));
        await using var row = await command.ExecuteReaderAsync(ct);
        if (!await row.ReadAsync(ct)) return (new Money(0, Currency.Usd), 0);
        var balance = Money.Parse(row.GetString(0), Currency.Usd);
        long version = row.GetInt64(1);
        if (version == 0 ? balance.Amount != 0 : row.IsDBNull(2) || ExactDecimal.Parse(row.GetString(2)) != balance.Amount)
            throw new InvalidOperationException("Wallet balance is missing its matching posting.");
        return (balance, version);
    }

    internal static async Task<Guid> PostAsync(SqliteTransaction tx, Guid member, (Money Balance, long Version) wallet, Money change,
        Guid origin, bool topup, string now, CancellationToken ct)
    {
        Guid entry = Guid.CreateVersion7();
        string after = ExactDecimal.Format(wallet.Balance.Add(change).Amount);
        using var update = Command(tx, "UPDATE wallets SET balance=$balance,version=version+1,updated_at=$now WHERE user_id=$member AND version=$version",
            ("$balance", after), ("$now", now), ("$member", member), ("$version", wallet.Version));
        if (await update.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Wallet update lost its target.");
        using var insert = Command(tx, """
            INSERT INTO wallet_entries(id,user_id,signed_amount,balance_after,posting_version,movement_kind,is_posting,idempotency_key,order_id,payment_id,reason)
            VALUES ($id,$member,$amount,$balance,$version,$kind,true,$key,$order,$payment,$reason)
            """, ("$id", entry), ("$member", member), ("$amount", ExactDecimal.Format(change.Amount)), ("$balance", after),
            ("$version", checked(wallet.Version + 1)), ("$kind", topup ? "wallet_topup" : "purchase_debit"),
            ("$key", topup ? $"payment-credit:{origin:D}" : $"checkout:{origin:D}"), ("$order", topup ? null : origin),
            ("$payment", topup ? origin : null), ("$reason", topup ? "verified_original_invoice" : "customer_request"));
        await insert.ExecuteNonQueryAsync(ct);
        return entry;
    }

    internal static SqliteCommand Command(SqliteTransaction tx, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = tx.Connection!.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
}
