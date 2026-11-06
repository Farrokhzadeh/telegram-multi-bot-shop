using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Platform.Core;
using Platform.Persistence;
namespace Platform.Migration;

public sealed partial class LegacySourceReader
{
    private const string WalletFields = "balance::text,balance_irr::text,lang,joined_at,COALESCE(last_seen_at,joined_at),is_active,is_banned,to_jsonb(u)->>'username',to_jsonb(u)->>'first_name',to_jsonb(u)->>'last_name',NULLIF(to_jsonb(u)->>'banned_at','')::timestamptz";
    private sealed record WalletProfile(decimal Balance, decimal Other, string Language, DateTime First, DateTime Last, bool Active, bool Banned, string? Username, string? FirstName, string? LastName, DateTime? BannedAt)
    {
        public override string ToString() => "WalletProfile [redacted]";
    }
    private static WalletProfile ReadWalletProfile(DbRow r, int n) => new(ExactDecimal.Parse(r.GetString(n)), ExactDecimal.Parse(r.GetString(n + 1)), r.GetString(n + 2), r.GetUtc(n + 3), r.GetUtc(n + 4), r.GetBoolean(n + 5), r.GetBoolean(n + 6), r.IsDBNull(n + 7) ? null : r.GetString(n + 7), r.IsDBNull(n + 8) ? null : r.GetString(n + 8), r.IsDBNull(n + 9) ? null : r.GetString(n + 9), r.IsDBNull(n + 10) ? null : r.GetUtc(n + 10));
    private static LegacyMemberState MemberState(WalletProfile user, DateTime observed) => new(string.IsNullOrEmpty(user.Username) ? null : user.Username, string.IsNullOrEmpty(user.FirstName) ? null : user.FirstName, string.IsNullOrEmpty(user.LastName) ? null : user.LastName, user.Active, user.Banned, observed, user.BannedAt);
    private static Guid WalletIdentity(Guid batch, Guid brand, string kind, string id) => new(SHA256.HashData(Encoding.UTF8.GetBytes("legacy-wallet-plan-v1:" + batch.ToString("N") + ":" + brand.ToString("N") + ":" + kind + ":" + id)).AsSpan(0, 16));
    public async Task<IReadOnlyList<LegacyWalletSnapshot>> PlanWalletsAsync(ReadOnlyMemory<byte> expectedSchema, Guid batch, Guid brand, IReadOnlyDictionary<string, LegacyWalletRetention> retained, DateTime observed, CancellationToken ct = default)
    {
        if (expectedSchema.Length != 32 || batch == Guid.Empty || brand == Guid.Empty || retained.Count is < 1 or > 100000 || observed.Kind != DateTimeKind.Utc || observed == DateTime.MaxValue || observed.Ticks % 10 != 0) throw new MigrationRejectedException();
        byte[] schema = expectedSchema.ToArray(); retained = new Dictionary<string, LegacyWalletRetention>(retained, StringComparer.Ordinal);
        await using var session = await OpenAsync(ct);
        byte[] actual = await SchemaAsync(session, ct);
        if (!CryptographicOperations.FixedTimeEquals(schema, actual) || await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM public.users"), r => r.GetInt64(0), ct) != retained.Count) throw new MigrationRejectedException();
        var users = new Dictionary<long, WalletProfile>(); long last = 0;
        while (true)
        {
            var page = await session.QueryManyAsync(new DbStatement("SELECT id," + WalletFields + " FROM public.users u WHERE id>$1 ORDER BY id LIMIT 1000", DbValue.Bigint(last)), r => (Id: r.GetInt64(0), User: ReadWalletProfile(r, 1)), 1000, ct);
            if (page.Count == 0) break;
            foreach (var row in page) if (!users.TryAdd(row.Id, row.User) || users.Count > retained.Count || !retained.ContainsKey(row.Id.ToString(CultureInfo.InvariantCulture))) throw new MigrationRejectedException();
            last = page[^1].Id;
        }
        if (users.Count != retained.Count) throw new MigrationRejectedException();
        var history = users.Keys.ToDictionary(id => id, _ => new List<LegacyRetainedTransaction>()); string after = ""; long seen = 0;
        long expected = await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM public.transactions"), r => r.GetInt64(0), ct);
        if (expected > 1000000) throw new MigrationRejectedException();
        while (true)
        {
            var page = await session.QueryManyAsync(new DbStatement("SELECT id,user_id,amount::text,currency,created_at FROM public.transactions WHERE id COLLATE \"C\" > $1 COLLATE \"C\" ORDER BY id COLLATE \"C\" LIMIT 1000", DbValue.Text(after)), r => (Id: r.GetString(0), User: r.GetInt64(1), Amount: ExactDecimal.Parse(r.GetString(2)), Currency: r.GetString(3), Created: r.GetUtc(4)), 1000, ct);
            if (page.Count == 0) break;
            foreach (var row in page)
            {
                if (++seen > expected || !history.TryGetValue(row.User, out var entries) || entries.Count >= 10000) throw new MigrationRejectedException();
                entries.Add(new(row.Id, row.Amount, row.Currency, row.Created));
            }
            after = page[^1].Id;
        }
        if (seen != expected) throw new MigrationRejectedException();
        var result = new List<LegacyWalletSnapshot>();
        foreach (var row in users.OrderBy(r => r.Key))
        {
            string id = row.Key.ToString(CultureInfo.InvariantCulture); var user = row.Value;
            retained[id].RequireHistory(history[row.Key]);
            var usd = history[row.Key].Where(h => h.Currency == "USD" && h.Amount != 0).Select(h => new LegacyHistoryRow(h.Id, WalletIdentity(batch, brand, "history", h.Id), h.Amount, "USD", h.Created, null)).ToArray();
            if (usd.Length > 1000) throw new MigrationRejectedException();
            var wallet = new LegacyWalletSnapshot(id, WalletIdentity(batch, brand, "user", id), WalletIdentity(batch, brand, "opening", id), row.Key, user.Language, user.First, user.Last, user.Balance, "USD", user.Other, Array.AsReadOnly(usd), MemberState(user, observed), retained[id]);
            wallet.Validate(); result.Add(wallet);
        }
        return result.AsReadOnly();
    }
}
