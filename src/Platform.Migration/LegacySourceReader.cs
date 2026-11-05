using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Platform.Core;
using Platform.Persistence;
namespace Platform.Migration;

public sealed record LegacySourceEndpoint(string Host, int Port, string Database, string Username)
{
    public override string ToString() => "LegacySourceEndpoint [redacted]";
}
public sealed record LegacySourceOrder(string Id, long UserId, long ProductId, int Quantity, decimal Total, string Currency, DateTime CreatedAt, string CheckoutSnapshot)
{
    public LegacyMapProof Proof(Guid target, Guid brand)
    {
        if (string.IsNullOrWhiteSpace(Id) || UserId <= 0 || ProductId <= 0 || Quantity <= 0 || Total < 0 || Currency is not ("USD" or "IRR") || CreatedAt.Kind != DateTimeKind.Utc || CheckoutSnapshot is null) throw new MigrationRejectedException();
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes, Encoding.UTF8, true);
        writer.Write("accabad-order-v1"); writer.Write(Id); writer.Write(UserId); writer.Write(ProductId); writer.Write(Quantity); writer.Write(ExactDecimal.Format(Total)); writer.Write(Currency); writer.Write(CreatedAt.Ticks); writer.Write(CheckoutSnapshot); writer.Flush();
        return new(LegacyProjection.Order, Id, target, brand, SHA256.HashData(bytes.ToArray()));
    }
    public override string ToString() => "LegacySourceOrder [redacted]";
}
public sealed class LegacySourceReader
{
    private readonly DbSessionFactory sessions;
    private readonly LegacySourceEndpoint endpoint;
    public LegacySourceReader(NpgsqlDataSource source, LegacySourceEndpoint approved)
    {
        endpoint = approved;
        if (string.IsNullOrWhiteSpace(approved.Host) || string.IsNullOrWhiteSpace(approved.Database) || string.IsNullOrWhiteSpace(approved.Username) || approved.Port is < 1 or > 65535) throw new MigrationRejectedException();
        sessions = new(source, new DbSessionOptions { ReadOnly = true, Isolation = IsolationLevel.RepeatableRead });
    }
    private async Task<DbSession> OpenAsync(CancellationToken ct)
    {
        var session = await sessions.BeginAsync(ct);
        try
        {
            if (!session.MatchesEndpoint(endpoint.Host, endpoint.Port, endpoint.Database, endpoint.Username) ||
                !await session.QuerySingleAsync(new DbStatement("SELECT current_setting('transaction_read_only')='on' AND current_setting('transaction_isolation')='repeatable read'"), r => r.GetBoolean(0), ct)) throw new MigrationRejectedException();
            return session;
        }
        catch { await session.DisposeAsync(); throw; }
    }
    public async Task<byte[]> InspectSchemaAsync(CancellationToken ct = default)
    {
        await using var session = await OpenAsync(ct);
        return await SchemaAsync(session, ct);
    }
    public async Task<LegacySourceOrder> ReadOrderAsync(VerifiedImportManifest manifest, string id, Guid target, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || id.Any(char.IsControl)) throw new MigrationRejectedException();
        await using var session = await OpenAsync(ct);
        if (Convert.ToHexStringLower(await SchemaAsync(session, ct)) != manifest.SourceSchemaDigest ||
            await session.QuerySingleAsync(new DbStatement("SELECT to_regclass('public.order_items') IS NOT NULL"), r => r.GetBoolean(0), ct)) throw new MigrationRejectedException();
        var row = await session.QuerySingleAsync(new DbStatement("SELECT id,user_id,product_id,qty,total::text,currency,created_at,checkout_snapshot::text FROM public.orders WHERE id=$1", DbValue.Text(id)),
            r => new LegacySourceOrder(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt32(3), ExactDecimal.Parse(r.GetString(4)), r.GetString(5), r.GetUtc(6), r.GetString(7)), ct);
        manifest.Require(row.Proof(target, manifest.TargetBrand));
        return row;
    }
    private static async Task<byte[]> SchemaAsync(DbSession session, CancellationToken ct)
    {
        var columns = await session.QueryManyAsync(new DbStatement("SELECT table_name,column_name,ordinal_position::text,data_type,udt_name,is_nullable,COALESCE(character_maximum_length::text,''),COALESCE(numeric_precision::text,''),COALESCE(numeric_scale::text,''),COALESCE(column_default,'') FROM information_schema.columns WHERE table_schema='public' ORDER BY table_name,column_name"),
            r => Enumerable.Range(0, 10).Select(r.GetString).ToArray(), 1000, ct);
        var migrations = await session.QueryManyAsync(new DbStatement("SELECT app,name FROM public.django_migrations ORDER BY app,name"), r => (App: r.GetString(0), Name: r.GetString(1)), 1000, ct);
        if (columns.Count == 0 || migrations.Count == 0 || migrations.Distinct().Count() != migrations.Count) throw new MigrationRejectedException();
        var constraints = await session.QueryManyAsync(new DbStatement("SELECT c.relname,k.conname,pg_get_constraintdef(k.oid,true) FROM pg_constraint k JOIN pg_class c ON c.oid=k.conrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' ORDER BY c.relname,k.conname"), r => new[] { r.GetString(0), r.GetString(1), r.GetString(2) }, 1000, ct);
        var indexes = await session.QueryManyAsync(new DbStatement("SELECT tablename,indexname,indexdef FROM pg_indexes WHERE schemaname='public' ORDER BY tablename,indexname"), r => new[] { r.GetString(0), r.GetString(1), r.GetString(2) }, 1000, ct);
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
        {
            writer.Write("accabad-source-schema-v2"); writer.Write(columns.Count);
            foreach (var row in columns) foreach (var value in row) writer.Write(value);
            writer.Write(constraints.Count); foreach (var row in constraints) foreach (var value in row) writer.Write(value);
            writer.Write(indexes.Count); foreach (var row in indexes) foreach (var value in row) writer.Write(value);
            writer.Write(migrations.Count);
            foreach (var row in migrations) { writer.Write(row.App); writer.Write(row.Name); }
        }
        return SHA256.HashData(bytes.ToArray());
    }
    public async Task<LegacyWalletSnapshot> ReadWalletAsync(VerifiedImportManifest manifest, long telegramId, Guid targetUser, Guid targetOpening, IReadOnlyDictionary<string, Guid> historyTargets, CancellationToken ct = default)
    {
        if (telegramId <= 0 || historyTargets.Count > 1000) throw new MigrationRejectedException();
        historyTargets = new Dictionary<string, Guid>(historyTargets, StringComparer.Ordinal);
        await using var session = await OpenAsync(ct);
        if (Convert.ToHexStringLower(await SchemaAsync(session, ct)) != manifest.SourceSchemaDigest) throw new MigrationRejectedException();
        var user = await session.QuerySingleAsync(new DbStatement("SELECT balance::text,balance_irr::text,lang,joined_at,COALESCE(last_seen_at,joined_at),is_active,is_banned FROM public.users WHERE id=$1", DbValue.Bigint(telegramId)),
            r => (Balance: ExactDecimal.Parse(r.GetString(0)), Other: ExactDecimal.Parse(r.GetString(1)), Language: r.GetString(2), First: r.GetUtc(3), Last: r.GetUtc(4), Active: r.GetBoolean(5), Banned: r.GetBoolean(6)), ct);
        if (!user.Active || user.Banned) throw new MigrationRejectedException();
        var historyRows = await session.QueryManyAsync(new DbStatement("SELECT id,amount::text,currency,created_at FROM public.transactions WHERE user_id=$1 ORDER BY created_at,id", DbValue.Bigint(telegramId)),
            r => (Id: r.GetString(0), Amount: r.GetString(1), Currency: r.GetString(2), Created: r.GetUtc(3)), 1000, ct);
        if (historyRows.Count != historyTargets.Count || historyRows.Any(h => !historyTargets.ContainsKey(h.Id))) throw new MigrationRejectedException();
        var history = historyRows.Select(h => new LegacyHistoryRow(h.Id, historyTargets[h.Id], ExactDecimal.Parse(h.Amount), h.Currency, h.Created, null)).ToArray();
        var result = new LegacyWalletSnapshot(telegramId.ToString(CultureInfo.InvariantCulture), targetUser, targetOpening, telegramId, user.Language, user.First, user.Last, user.Balance, "USD", user.Other, history);
        result.Validate();
        foreach (var proof in result.Proofs(manifest.TargetBrand)) manifest.Require(proof);
        return result;
    }
}

