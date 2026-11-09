using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Platform.Persistence;
using Platform.Security;
namespace Platform.Migration;

public sealed record LegacyCouponRecord(ArchivedLegacyRecord Record, Guid Revision, Guid? Product, Guid? Member, string Kind, Guid? PaidSourceOrder = null)
{
    internal CouponImportTerms Terms()
    {
        if (Record.Source.Projection != LegacyProjection.DiscountRecord || Revision == Guid.Empty || Product == Guid.Empty || Member is null || Member == Guid.Empty || PaidSourceOrder == Guid.Empty || Kind is not ("promotion" or "goodwill" or "prepaid_entitlement")) throw new MigrationRejectedException();
        using var document = Record.Source.Open(); var source = document.RootElement;
        decimal percent = LegacyValues.Number(source, "pct_off"); int maximum = source.GetProperty("max_uses").GetInt32(), used = source.GetProperty("current_uses").GetInt32();
        long? product = source.GetProperty("product_id").ValueKind == System.Text.Json.JsonValueKind.Null ? null : source.GetProperty("product_id").GetInt64();
        long? member = source.GetProperty("assigned_user_id").ValueKind == System.Text.Json.JsonValueKind.Null ? null : source.GetProperty("assigned_user_id").GetInt64();
        string code = LegacyValues.Text(source, "code");
        if (code != code.Trim().ToUpperInvariant() || code.Length is < 1 or > 64 || code.Any(char.IsControl) || percent is <= 0 or > 100 || product <= 0 || member <= 0 || maximum <= 0 || used < 0 || used > maximum ||
            (Product is null) != (product is null) || (Member is null) != (member is null) || (Kind == "prepaid_entitlement" ? percent != 100 || Product is null || Member is null || PaidSourceOrder is null : PaidSourceOrder is not null)) throw new MigrationRejectedException();
        return new(code, percent, maximum, used, source.GetProperty("active").GetBoolean(), product, member);
    }
    public IReadOnlyList<LegacyMapProof> Proofs(BrandContext context, SecretSnapshot secrets)
    {
        _ = Terms(); var source = Record.Source.Proof(Record.Target, context, secrets);
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes, Encoding.UTF8, true);
        writer.Write("legacy-coupon-target-v2-disabled"); writer.Write(source.Fingerprint.Span); writer.Write(Revision.ToByteArray()); writer.Write(Product?.ToByteArray() ?? new byte[16]); writer.Write(Member?.ToByteArray() ?? new byte[16]);
        writer.Write(Kind); writer.Write(PaidSourceOrder?.ToByteArray() ?? new byte[16]); writer.Write(Record.Reference); writer.Write(Record.Digest); writer.Flush(); byte[] digest = SHA256.HashData(bytes.ToArray());
        return [source, new(LegacyProjection.DiscountSnapshot, Record.Source.Id, Record.Target, context.BrandId, digest), new(LegacyProjection.DiscountRevision, Record.Source.Id, Revision, context.BrandId, digest)];
    }
    public override string ToString() => "LegacyCouponRecord [redacted]";
}
internal sealed record CouponImportTerms(string Code, decimal Percent, int Maximum, int Used, bool Active, long? Product, long? Member);
public sealed class LegacyCouponImportWorkflow(DbSessionFactory sessions, SecretSnapshot secrets)
{
    public async Task ImportAsync(BrandContext context, VerifiedImportManifest manifest, LegacyCouponRecord row, CancellationToken ct = default)
    {
        var terms = row.Terms(); var proofs = row.Proofs(context, secrets);
        if (context.BrandId != manifest.TargetBrand || !new LegacyRecordArchive(secrets).Matches(context, row.Record, row.Record.Evidence)) throw new MigrationRejectedException();
        foreach (var proof in proofs) manifest.Require(proof);
        await using var session = await sessions.BeginAsync(ct); await LegacyMapStore.RequireDestinationAsync(session, manifest, ct);
        if (terms.Member is long member) await LegacyValues.RequireMemberAsync(session, context, manifest, row.Member!.Value, member, ct);
        if (terms.Product is long product && !manifest.Proofs.Any(p => p.Projection is LegacyProjection.Product or LegacyProjection.ProductRecord && p.LegacyId == product.ToString(CultureInfo.InvariantCulture) && p.Target == row.Product)) throw new MigrationRejectedException();
        if (row.PaidSourceOrder is Guid paid && (!manifest.Proofs.Any(p => p.Projection is LegacyProjection.Order or LegacyProjection.OrderRecord && p.Target == paid) ||
            !await session.QuerySingleAsync(new DbStatement("""
                SELECT EXISTS(SELECT 1 FROM shop.orders o JOIN shop.users u ON u.id=o.user_id WHERE o.id=$1 AND o.order_kind='prepaid_units' AND o.payment_status='paid'
                    AND o.user_id=$2 AND o.product_id=$3 AND o.quantity=$4 AND u.brand_id=$5
                    AND EXISTS(SELECT 1 FROM shop.payments p WHERE p.order_id=o.id AND p.status='settled' AND p.legacy_kind IS NOT NULL AND p.evidence_asset_ref IS NOT NULL AND p.evidence_digest IS NOT NULL))
                """, DbValue.Uuid(paid), DbValue.Uuid(row.Member!.Value), DbValue.Uuid(row.Product!.Value), DbValue.Integer(terms.Maximum), DbValue.Uuid(context.BrandId)), r => r.GetBoolean(0), ct))) throw new MigrationRejectedException();
        bool first = await LegacyMapStore.EnsureAsync(session, manifest, proofs[0], ct);
        foreach (var proof in proofs.Skip(1)) if (await LegacyMapStore.EnsureAsync(session, manifest, proof, ct) != first) throw new MigrationRejectedException();
        if (first)
        {
            string key = row.Record.Evidence.KeyId; byte[] plain = Encoding.UTF8.GetBytes(terms.Code), encrypted;
            try { encrypted = new BrandCipher(secrets).Encrypt(context, "discounts", row.Record.Target, "code_ciphertext", plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            if (key != secrets.CurrentBrandKeyId(context)) throw new MigrationRejectedException();
            await session.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.discounts(id,brand_id,code_digest,code_ciphertext,code_key_id,display_prefix,issuance_batch_id,kind,percent_off,product_id,eligible_user_id,source_order_id,max_total_uses,usage_adjustment_units,active)
                VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15)
                """, DbValue.Uuid(row.Record.Target), DbValue.Uuid(context.BrandId), DbValue.Binary(Digest(context, key, terms.Code)), DbValue.Binary(encrypted), DbValue.Text(key), DbValue.Text(terms.Code[..Math.Min(3, terms.Code.Length)]), DbValue.Uuid(manifest.Batch), DbValue.Text(row.Kind),
                DbValue.Numeric(terms.Percent), LegacyValues.Uuid(row.Product), LegacyValues.Uuid(row.Member), LegacyValues.Uuid(row.PaidSourceOrder), DbValue.Bigint(terms.Maximum), DbValue.Bigint(terms.Used), DbValue.Boolean(false)), ct);
            await session.ExecuteAsync(new DbStatement("INSERT INTO shop.audit_events(id,actor_kind,brand_id,action,entity_kind,entity_id,reason,command_sha256,correlation_id,new_revision) VALUES($1,'system',$2,'legacy_import','discount',$3,$4,$5,$6,0)", DbValue.Uuid(LegacyValues.AuditId(manifest, row.Record)), DbValue.Uuid(context.BrandId), DbValue.Uuid(row.Record.Target), DbValue.Text(row.Record.Reference), DbValue.Binary(row.Record.Digest), DbValue.Uuid(manifest.Batch)), ct);
            await session.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.discount_revisions(id,discount_id,revision_no,audit_event_id,percent_off,max_total_uses,usage_adjustment_units,redeemed_count_at_revision,held_units_at_revision,active,product_id,eligible_user_id)
                VALUES($1,$2,0,$3,$4,$5,$6,0,0,$7,$8,$9)
                """, DbValue.Uuid(row.Revision), DbValue.Uuid(row.Record.Target), DbValue.Uuid(LegacyValues.AuditId(manifest, row.Record)), DbValue.Numeric(terms.Percent), DbValue.Bigint(terms.Maximum), DbValue.Bigint(terms.Used), DbValue.Boolean(false), LegacyValues.Uuid(row.Product), LegacyValues.Uuid(row.Member)), ct);
        }
        if (!await MatchesAsync(session, context, manifest, row, ct) || !await LegacyValues.MatchesAuditAsync(session, manifest, row.Record, "discount", ct)) throw new MigrationRejectedException();
        await session.CommitAsync(ct);
    }
    private byte[] Digest(BrandContext context, string key, string code)
    {
        byte[] bytes = Encoding.UTF8.GetBytes("coupon-lookup-v1:" + context.BrandId.ToString("D") + ":" + code);
        try { return secrets.UseBrandKey(context, key, secret => HMACSHA256.HashData(secret, bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    internal async Task<bool> MatchesAsync(DbSession session, BrandContext context, VerifiedImportManifest manifest, LegacyCouponRecord row, CancellationToken ct)
    {
        var t = row.Terms();
        bool fields = await session.QuerySingleAsync(new DbStatement("""
            SELECT EXISTS(SELECT 1 FROM shop.discounts d JOIN shop.discount_revisions r ON r.discount_id=d.id WHERE d.id=$1 AND d.brand_id=$2 AND d.code_digest=$3 AND d.code_key_id=$4
                AND d.issuance_batch_id=$5 AND d.kind=$6 AND d.percent_off=$7 AND d.product_id IS NOT DISTINCT FROM $8 AND d.eligible_user_id IS NOT DISTINCT FROM $9 AND d.source_order_id IS NOT DISTINCT FROM $10
                AND d.max_total_uses=$11 AND d.redeemed_count=0 AND d.usage_adjustment_units=$12 AND d.active=$13 AND d.revision=0 AND d.created_by_admin_id IS NULL AND d.max_uses_per_user IS NULL
                AND d.starts_at IS NULL AND d.ends_at IS NULL AND r.id=$14 AND r.revision_no=0 AND r.audit_event_id=$15 AND r.percent_off=d.percent_off AND r.max_total_uses=d.max_total_uses
                AND r.usage_adjustment_units=d.usage_adjustment_units AND r.redeemed_count_at_revision=0 AND r.held_units_at_revision=0 AND r.active=d.active AND r.product_id IS NOT DISTINCT FROM d.product_id
                AND r.eligible_user_id IS NOT DISTINCT FROM d.eligible_user_id AND NOT EXISTS(SELECT 1 FROM shop.discount_uses WHERE discount_id=d.id))
            """, DbValue.Uuid(row.Record.Target), DbValue.Uuid(context.BrandId), DbValue.Binary(Digest(context, row.Record.Evidence.KeyId, t.Code)), DbValue.Text(row.Record.Evidence.KeyId), DbValue.Uuid(manifest.Batch), DbValue.Text(row.Kind), DbValue.Numeric(t.Percent), LegacyValues.Uuid(row.Product), LegacyValues.Uuid(row.Member), LegacyValues.Uuid(row.PaidSourceOrder), DbValue.Bigint(t.Maximum), DbValue.Bigint(t.Used), DbValue.Boolean(false), DbValue.Uuid(row.Revision), DbValue.Uuid(LegacyValues.AuditId(manifest, row.Record))), r => r.GetBoolean(0), ct);
        if (!fields) return false;
        byte[] encrypted = await session.QuerySingleAsync(new DbStatement("SELECT code_ciphertext FROM shop.discounts WHERE id=$1", DbValue.Uuid(row.Record.Target)), r => r.GetBinary(0), ct);
        byte[] plain = new BrandCipher(secrets).Decrypt(context, "discounts", row.Record.Target, "code_ciphertext", row.Record.Evidence.KeyId, encrypted), expected = Encoding.UTF8.GetBytes(t.Code);
        try { return CryptographicOperations.FixedTimeEquals(plain, expected); }
        finally { CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(expected); }
    }
}
