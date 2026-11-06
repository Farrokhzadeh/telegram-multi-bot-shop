using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Platform.Core;
using Platform.Persistence;
using Platform.Security;
namespace Platform.Migration;

public sealed record LegacySourceEndpoint(string Host, int Port, string Database, string Username)
{
    public override string ToString() => "LegacySourceEndpoint [redacted]";
}
public sealed record LegacySourceOrder(string Id, long UserId, long ProductId, int Quantity, decimal Total, string Currency, DateTime CreatedAt, string CheckoutSnapshot)
{
    public LegacyMapProof Proof(Guid target, Guid brand)
    {
        if (string.IsNullOrWhiteSpace(Id) || UserId <= 0 || ProductId <= 0 || Quantity <= 0 || Total < 0 || Currency is not ("USD" or "IRR") || CreatedAt.Kind != DateTimeKind.Utc || CreatedAt == DateTime.MinValue || CreatedAt == DateTime.MaxValue || CreatedAt.Ticks % 10 != 0 || CheckoutSnapshot is null) throw new MigrationRejectedException();
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes, Encoding.UTF8, true);
        writer.Write("accabad-order-v1"); writer.Write(Id); writer.Write(UserId); writer.Write(ProductId); writer.Write(Quantity); writer.Write(ExactDecimal.Format(Total)); writer.Write(Currency); writer.Write(CreatedAt.Ticks); writer.Write(CheckoutSnapshot); writer.Flush();
        return new(LegacyProjection.Order, Id, target, brand, SHA256.HashData(bytes.ToArray()));
    }
    public override string ToString() => "LegacySourceOrder [redacted]";
}
public sealed record LegacySourceDiscount(string Code, long? ProductId, long? AssignedUserId, decimal Percent, int MaximumUses, int CurrentUses, bool Active)
{
    public LegacyMapProof Proof(Guid target, BrandContext context, SecretSnapshot secrets)
    {
        if (string.IsNullOrWhiteSpace(Code) || Code.Length > 64 || Code.Any(char.IsControl) || ProductId <= 0 || AssignedUserId <= 0 || Percent <= 0 || Percent > 100 || MaximumUses <= 0 || CurrentUses < 0 || CurrentUses > MaximumUses) throw new MigrationRejectedException();
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes, Encoding.UTF8, true);
        writer.Write("accabad-discount-v1"); writer.Write(Code); writer.Write(ProductId.HasValue); if (ProductId is long product) writer.Write(product);
        writer.Write(AssignedUserId.HasValue); if (AssignedUserId is long user) writer.Write(user);
        writer.Write(ExactDecimal.Format(Percent)); writer.Write(MaximumUses); writer.Write(CurrentUses); writer.Write(Active); writer.Flush();
        string key = secrets.CurrentBrandKeyId(context);
        byte[] identity = Encoding.UTF8.GetBytes("legacy-discount-identity-v1:" + context.BrandId.ToString("D") + ":" + Code);
        byte[] canonical = bytes.ToArray();
        try
        {
            byte[] digest = secrets.UseBrandKey(context, key, secret => HMACSHA256.HashData(secret, identity));
            byte[] fingerprint = secrets.UseBrandKey(context, key, secret => HMACSHA256.HashData(secret, canonical));
            return new(LegacyProjection.Discount, Convert.ToHexStringLower(digest), target, context.BrandId, fingerprint);
        }
        finally { CryptographicOperations.ZeroMemory(identity); CryptographicOperations.ZeroMemory(canonical); }
    }
    public override string ToString() => "LegacySourceDiscount [redacted]";
}
public sealed record LegacySourceProduct(long Id, string Name, decimal Price, decimal PriceIrr, bool AutoPrice, string? Description, string? Rules, string? Icon, string? Guidance, string ProductType, bool Active, bool Removed, string? RateId, string? MediaType, string? MediaId, string? ExpectedDate, string? ServiceHours, int Stock)
{
    public LegacyMapProof Proof(Guid target, Guid brand)
    {
        if (Id <= 0 || string.IsNullOrWhiteSpace(Name) || Name.Length > 255 || Price < 0 || PriceIrr < 0 || Stock < 0 || ProductType is not ("credential" or "service" or "api" or "preorder") || !Active || Removed) throw new MigrationRejectedException();
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes, Encoding.UTF8, true);
        writer.Write("accabad-product-v1"); writer.Write(Id); writer.Write(Name); writer.Write(ExactDecimal.Format(Price)); writer.Write(ExactDecimal.Format(PriceIrr)); writer.Write(AutoPrice);
        foreach (var value in new[] { Description, Rules, Icon, Guidance, RateId, MediaType, MediaId, ExpectedDate, ServiceHours }) { writer.Write(value is not null); if (value is not null) writer.Write(value); }
        writer.Write(ProductType); writer.Write(Active); writer.Write(Removed); writer.Write(Stock); writer.Flush();
        return new(LegacyProjection.Product, Id.ToString(CultureInfo.InvariantCulture), target, brand, SHA256.HashData(bytes.ToArray()));
    }
    public override string ToString() => "LegacySourceProduct [redacted]";
}
public sealed partial class LegacySourceReader
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
    public async Task<long> VerifyOrderCoverageAsync(VerifiedImportManifest manifest, CancellationToken ct = default)
    {
        var expected = manifest.Proofs.Where(p => p.Projection == LegacyProjection.Order).ToDictionary(p => p.LegacyId, StringComparer.Ordinal);
        await using var session = await OpenAsync(ct);
        if (Convert.ToHexStringLower(await SchemaAsync(session, ct)) != manifest.SourceSchemaDigest ||
            await session.QuerySingleAsync(new DbStatement("SELECT to_regclass('public.order_items') IS NOT NULL"), r => r.GetBoolean(0), ct)) throw new MigrationRejectedException();
        long count = await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM public.orders"), r => r.GetInt64(0), ct);
        if (count != expected.Count) throw new MigrationRejectedException();
        long seen = 0; string last = "";
        while (true)
        {
            var rows = await session.QueryManyAsync(new DbStatement("SELECT id,user_id,product_id,qty,total::text,currency,created_at,checkout_snapshot::text FROM public.orders WHERE id COLLATE \"C\" > $1 COLLATE \"C\" ORDER BY id COLLATE \"C\" LIMIT 1000", DbValue.Text(last)),
                r => new LegacySourceOrder(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt32(3), ExactDecimal.Parse(r.GetString(4)), r.GetString(5), r.GetUtc(6), r.GetString(7)), 1000, ct);
            if (rows.Count == 0) break;
            foreach (var row in rows)
            {
                if (!expected.TryGetValue(row.Id, out var proof)) throw new MigrationRejectedException();
                manifest.Require(row.Proof(proof.Target, manifest.TargetBrand));
                seen++;
            }
            last = rows[^1].Id;
        }
        if (seen != count) throw new MigrationRejectedException();
        return seen;
    }
    public async Task<LegacySourceDiscount> ReadDiscountAsync(BrandContext context, SecretSnapshot secrets, VerifiedImportManifest manifest, string code, Guid target, CancellationToken ct = default)
    {
        if (context.BrandId != manifest.TargetBrand || string.IsNullOrWhiteSpace(code) || code.Length > 64 || code.Any(char.IsControl)) throw new MigrationRejectedException();
        await using var session = await OpenAsync(ct);
        if (Convert.ToHexStringLower(await SchemaAsync(session, ct)) != manifest.SourceSchemaDigest) throw new MigrationRejectedException();
        var row = await session.QuerySingleAsync(new DbStatement("SELECT code,product_id,assigned_user_id,pct_off::text,max_uses,current_uses,active FROM public.discounts WHERE code=$1", DbValue.Text(code)),
            r => new LegacySourceDiscount(r.GetString(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.IsDBNull(2) ? null : r.GetInt64(2), ExactDecimal.Parse(r.GetString(3)), r.GetInt32(4), r.GetInt32(5), r.GetBoolean(6)), ct);
        manifest.Require(row.Proof(target, context, secrets));
        return row;
    }
    public async Task<LegacySourceProduct> ReadProductAsync(VerifiedImportManifest manifest, long id, Guid target, CancellationToken ct = default)
    {
        if (id <= 0) throw new MigrationRejectedException();
        await using var session = await OpenAsync(ct);
        if (Convert.ToHexStringLower(await SchemaAsync(session, ct)) != manifest.SourceSchemaDigest) throw new MigrationRejectedException();
        var row = await session.QuerySingleAsync(new DbStatement("SELECT id,name,price::text,price_irr::text,auto_price,description,rules,icon_custom_emoji_id,post_purchase,product_type,active,is_removed,pricing_rate_id::text,post_purchase_media_type,post_purchase_media_id,expected_availability_date::text,service_target_hours::text,stock FROM public.products WHERE id=$1", DbValue.Bigint(id)),
            r => new LegacySourceProduct(r.GetInt64(0), r.GetString(1), ExactDecimal.Parse(r.GetString(2)), ExactDecimal.Parse(r.GetString(3)), r.GetBoolean(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), r.GetString(9), r.GetBoolean(10), r.GetBoolean(11), r.IsDBNull(12) ? null : r.GetString(12), r.IsDBNull(13) ? null : r.GetString(13), r.IsDBNull(14) ? null : r.GetString(14), r.IsDBNull(15) ? null : r.GetString(15), r.IsDBNull(16) ? null : r.GetString(16), r.GetInt32(17)), ct);
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
    public async Task<LegacyWalletSnapshot> ReadWalletAsync(VerifiedImportManifest manifest, long telegramId, Guid targetUser, Guid targetOpening, IReadOnlyDictionary<string, Guid> historyTargets, CancellationToken ct = default, DateTime? observedAt = null, LegacyWalletRetention? retention = null)
    {
        if (telegramId <= 0 || historyTargets.Count > 1000) throw new MigrationRejectedException();
        historyTargets = new Dictionary<string, Guid>(historyTargets, StringComparer.Ordinal);
        await using var session = await OpenAsync(ct);
        if (Convert.ToHexStringLower(await SchemaAsync(session, ct)) != manifest.SourceSchemaDigest) throw new MigrationRejectedException();
        var user = await session.QuerySingleAsync(new DbStatement("SELECT " + WalletFields + " FROM public.users u WHERE id=$1", DbValue.Bigint(telegramId)), r => ReadWalletProfile(r, 0), ct);
        bool profile = !user.Active || user.Banned || !string.IsNullOrEmpty(user.Username) || !string.IsNullOrEmpty(user.FirstName) || !string.IsNullOrEmpty(user.LastName);
        if (profile && observedAt is null) throw new MigrationRejectedException();
        long expectedHistory = await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM public.transactions WHERE user_id=$1", DbValue.Bigint(telegramId)), r => r.GetInt64(0), ct);
        if (expectedHistory > 10000) throw new MigrationRejectedException();
        var original = new List<LegacyRetainedTransaction>(); string lastHistory = "";
        while (true)
        {
            var page = await session.QueryManyAsync(new DbStatement("SELECT id,amount::text,currency,created_at FROM public.transactions WHERE user_id=$1 AND id COLLATE \"C\" > $2 COLLATE \"C\" ORDER BY id COLLATE \"C\" LIMIT 1000", DbValue.Bigint(telegramId), DbValue.Text(lastHistory)),
                r => new LegacyRetainedTransaction(r.GetString(0), ExactDecimal.Parse(r.GetString(1)), r.GetString(2), r.GetUtc(3)), 1000, ct);
            if (page.Count == 0) break;
            original.AddRange(page); if (original.Count > expectedHistory) throw new MigrationRejectedException(); lastHistory = page[^1].Id;
        }
        if (original.Count != expectedHistory) throw new MigrationRejectedException();
        if (retention is null)
        {
            foreach (var row in original.Where(r => r.Amount == 0)) if (!manifest.Proofs.Any(p => p.Projection == LegacyProjection.ZeroTransaction && p.LegacyId == row.Id)) throw new MigrationRejectedException();
        }
        else { retention.Require(manifest); retention.RequireHistory(original); }
        var historyRows = original.Where(r => r.Amount != 0 && (retention is null || r.Currency == "USD")).ToArray();
        if (historyRows.Length != historyTargets.Count || historyRows.Any(h => !historyTargets.ContainsKey(h.Id))) throw new MigrationRejectedException();
        var history = historyRows.Select(h => new LegacyHistoryRow(h.Id, historyTargets[h.Id], h.Amount, h.Currency, h.Created, null)).ToArray();
        var result = new LegacyWalletSnapshot(telegramId.ToString(CultureInfo.InvariantCulture), targetUser, targetOpening, telegramId, user.Language, user.First <= user.Last ? user.First : user.Last, user.First <= user.Last ? user.Last : user.First, user.Balance, "USD", user.Other, history, observedAt is DateTime observed ? MemberState(user, observed) : null, retention, SourceClock(user));
        result.Validate();
        foreach (var proof in result.Proofs(manifest.TargetBrand)) manifest.Require(proof);
        return result;
    }
}

