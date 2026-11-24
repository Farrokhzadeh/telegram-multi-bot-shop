using Microsoft.EntityFrameworkCore;

namespace Platform.Persistence;

public sealed record SqliteStageHealth(long Brands, long Customers, long Orders, long WalletPostings,
    long TelegramDeliveries, long OpenJobs);

public static class SqliteStageHealthProbe
{
    public static Task<SqliteStageHealth> VerifyAsync(SqliteStagingConfiguration stage, CancellationToken ct = default) =>
        stage.Sessions.RunAsync(true, async (_, tx, token) =>
        {
            var data = await SqliteSessionFactory.StepEfAsync(tx, false, async (session, cancel) => new SqliteStageHealth(await session.Data.Set<Entities.Brand>().LongCountAsync(cancel), await session.Data.Set<Entities.User>().LongCountAsync(cancel), await session.Data.Set<Entities.Order>().LongCountAsync(cancel), await session.Data.Set<Entities.WalletEntry>().LongCountAsync(row => row.IsPosting, cancel), await session.Data.Set<Entities.TelegramDelivery>().LongCountAsync(cancel), await session.Data.Set<Entities.Job>().LongCountAsync(row => row.Status == "pending" || row.Status == "retry" || row.Status == "leased", cancel)), token);
            using var check = SqliteWallet.Command(tx, "PRAGMA quick_check");
            if (await check.ExecuteScalarAsync(token) is not string result ||
                result != "ok") throw new InvalidOperationException("SQLite staging database integrity failed.");
            return data;
        }, ct);
}
