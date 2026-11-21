using Microsoft.Data.Sqlite;

namespace Platform.Persistence;

public sealed record SqliteStageHealth(long Brands, long Customers, long Orders, long WalletPostings,
    long TelegramDeliveries, long OpenJobs);

public static class SqliteStageHealthProbe
{
    public static Task<SqliteStageHealth> VerifyAsync(SqliteStagingConfiguration stage, CancellationToken ct = default) =>
        stage.Sessions.RunAsync(true, async (_, tx, token) =>
        {
            using var command = SqliteWallet.Command(tx, """
                SELECT
                    (SELECT count(*) FROM brands),
                    (SELECT count(*) FROM users),
                    (SELECT count(*) FROM orders),
                    (SELECT count(*) FROM wallet_entries WHERE is_posting=1),
                    (SELECT count(*) FROM telegram_deliveries),
                    (SELECT count(*) FROM jobs WHERE status IN ('pending','retry','leased'))
                """);
            await using var rows = await command.ExecuteReaderAsync(token);
            if (!await rows.ReadAsync(token)) throw new InvalidOperationException("SQLite staging is unavailable.");
            var data = new SqliteStageHealth(rows.GetInt64(0), rows.GetInt64(1), rows.GetInt64(2),
                rows.GetInt64(3), rows.GetInt64(4), rows.GetInt64(5));
            await rows.CloseAsync();
            using var check = SqliteWallet.Command(tx, "PRAGMA quick_check");
            if (await check.ExecuteScalarAsync(token) is not string result ||
                result != "ok") throw new InvalidOperationException("SQLite staging database integrity failed.");
            return data;
        }, ct);
}
