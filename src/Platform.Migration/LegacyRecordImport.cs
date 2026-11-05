using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpgsqlTypes;
using Platform.Core;
using Platform.Persistence;
using Platform.Security;
namespace Platform.Migration;
internal static class LegacyValues
{
    internal static string Text(JsonElement row, string name) => row.GetProperty(name).GetString() ?? throw new MigrationRejectedException();
    internal static string? Optional(JsonElement row, string name) => row.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null && value.GetString() is string text && text.Length > 0 ? text : null;
    internal static decimal Number(JsonElement row, string name) { var value = row.GetProperty(name); return ExactDecimal.Parse(value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText()); }
    internal static DateTime Time(JsonElement row, string name)
    {
        string text = Text(row, name);
        if (!text.EndsWith('Z') && !(text.Length >= 6 && text[^6] is '+' or '-' && text[^3] == ':')) throw new MigrationRejectedException();
        var value = DateTimeOffset.Parse(text, CultureInfo.InvariantCulture).UtcDateTime;
        if (value == DateTime.MinValue || value == DateTime.MaxValue || value.Ticks % 10 != 0) throw new MigrationRejectedException(); return value;
    }
    internal static DbValue Uuid(Guid? value) => value is Guid id ? DbValue.Uuid(id) : DbValue.Null(NpgsqlDbType.Uuid);
    internal static DbValue String(string? value) => value is null ? DbValue.Null(NpgsqlDbType.Text) : DbValue.Text(value);
    internal static DbValue Date(DateTime? value) => value is DateTime time ? DbValue.Utc(time) : DbValue.Null(NpgsqlDbType.TimestampTz);
    internal static Guid AuditId(VerifiedImportManifest manifest, ArchivedLegacyRecord row) => new(SHA256.HashData(Encoding.UTF8.GetBytes("legacy-record-audit-v1:" + manifest.Batch.ToString("N") + ":" + row.Source.Projection + ":" + row.Source.Id)).AsSpan(0, 16));
    internal static async Task RequireMemberAsync(DbSession session, BrandContext context, VerifiedImportManifest manifest, Guid member, long telegram, CancellationToken ct)
    {
        if (!manifest.Proofs.Any(p => p.Projection == LegacyProjection.User && p.Target == member && p.LegacyId == telegram.ToString(CultureInfo.InvariantCulture)) ||
            !await session.QuerySingleAsync(new DbStatement("SELECT EXISTS(SELECT 1 FROM shop.users u JOIN shop.brands b ON b.id=u.brand_id WHERE u.id=$1 AND u.telegram_id=$2 AND b.id=$3 AND b.bot_id=$4)", DbValue.Uuid(member), DbValue.Bigint(telegram), DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId)), r => r.GetBoolean(0), ct)) throw new MigrationRejectedException();
    }
    internal static Task AuditAsync(DbSession session, VerifiedImportManifest manifest, ArchivedLegacyRecord row, string kind, CancellationToken ct) => session.ExecuteAsync(new DbStatement("INSERT INTO shop.audit_events(id,actor_kind,brand_id,action,entity_kind,entity_id,reason,command_sha256,correlation_id) VALUES($1,'system',$2,'legacy_import',$3,$4,$5,$6,$7)", DbValue.Uuid(AuditId(manifest, row)), DbValue.Uuid(manifest.TargetBrand), DbValue.Text(kind), DbValue.Uuid(row.Target), DbValue.Text(row.Reference), DbValue.Binary(row.Digest), DbValue.Uuid(manifest.Batch)), ct);
    internal static Task<bool> MatchesAuditAsync(DbSession session, VerifiedImportManifest manifest, ArchivedLegacyRecord row, string kind, CancellationToken ct) => session.QuerySingleAsync(new DbStatement("SELECT EXISTS(SELECT 1 FROM shop.audit_events WHERE id=$1 AND actor_kind='system' AND brand_id=$2 AND action='legacy_import' AND entity_kind=$3 AND entity_id=$4 AND reason=$5 AND command_sha256=$6 AND correlation_id=$7)", DbValue.Uuid(AuditId(manifest, row)), DbValue.Uuid(manifest.TargetBrand), DbValue.Text(kind), DbValue.Uuid(row.Target), DbValue.Text(row.Reference), DbValue.Binary(row.Digest), DbValue.Uuid(manifest.Batch)), r => r.GetBoolean(0), ct);
}
public sealed record LegacyHistoricalRecord(ArchivedLegacyRecord Record, Guid Member, string HistoricalBrandName)
{
    internal LegacyOrderTerms Terms()
    {
        if (Record.Source.Projection != LegacyProjection.OrderRecord || Member == Guid.Empty || string.IsNullOrWhiteSpace(HistoricalBrandName) || HistoricalBrandName.Length > 160) throw new MigrationRejectedException();
        try
        {
            using var document = Record.Source.Open(); var row = document.RootElement; var snapshot = row.GetProperty("checkout_snapshot");
            int quantity = row.GetProperty("qty").GetInt32(); long product = row.GetProperty("product_id").GetInt64(); decimal total = LegacyValues.Number(row, "total");
            string currency = LegacyValues.Text(row, "currency"), method = LegacyValues.Text(snapshot, "payment_method"), type = LegacyValues.Text(snapshot, "product_type"), title = LegacyValues.Text(snapshot, "product_name");
            decimal original = LegacyValues.Number(snapshot, "original_usd"), net = LegacyValues.Number(snapshot, "total_usd"), originalToman = LegacyValues.Number(snapshot, "original_toman"), netToman = LegacyValues.Number(snapshot, "total_toman");
            int discounted = snapshot.GetProperty("discount_quantity").GetInt32(); string? code = LegacyValues.Optional(snapshot, "discount_code"), percent = LegacyValues.Optional(snapshot, "discount_pct");
            if (Record.Source.Id.Length != 16 || !Record.Source.Id.StartsWith("ORD-", StringComparison.Ordinal) || Record.Source.Id.Skip(4).Any(c => !char.IsAsciiHexDigitUpper(c)) ||
                quantity <= 0 || product <= 0 || snapshot.GetProperty("version").GetInt32() != 1 || snapshot.GetProperty("product_id").GetInt64() != product || snapshot.GetProperty("quantity").GetInt32() != quantity ||
                LegacyValues.Text(snapshot, "currency") != currency || string.IsNullOrWhiteSpace(title) || title.Length > 160 || original <= 0 || net < 0 || net > original || originalToman < 0 || netToman < 0 || netToman > originalToman ||
                LegacyValues.Number(snapshot, "savings_usd") != original - net || LegacyValues.Number(snapshot, "savings_toman") != originalToman - netToman || original / quantity * quantity != original ||
                type is not ("credential" or "service" or "api" or "preorder") || method is not ("wallet" or "crypto" or "bank") || (method == "bank" ? currency != "IRR" || total != netToman || netToman <= 0 : currency != "USD" || total != net)) throw new MigrationRejectedException();
            if (code is null) { if (percent is not null || discounted != 0 || net != original || netToman != originalToman) throw new MigrationRejectedException(); }
            else
            {
                decimal pct = ExactDecimal.Parse(percent ?? throw new MigrationRejectedException());
                if (string.IsNullOrWhiteSpace(code) || code.Length > 64 || pct is <= 0 or > 100 || discounted < 1 || discounted > quantity || net != decimal.Round(original - original / quantity * discounted * pct / 100, 2, MidpointRounding.ToEven)) throw new MigrationRejectedException();
            }
            bool free = net == 0;
            if ((free && (method == "bank" || netToman != 0 || discounted != quantity || percent is null || ExactDecimal.Parse(percent) != 100)) || (!free && net / quantity * quantity != net) || (method == "bank" && netToman / quantity * quantity != netToman)) throw new MigrationRejectedException();
            return new(row.GetProperty("user_id").GetInt64(), quantity, title, free ? original / quantity : net / quantity, method == "bank" ? netToman / quantity : null,
                free ? "no_payment_required" : method, method == "bank" ? "TOMAN" : "USDT", type == "service" ? "manual" : type == "api" ? "supplier" : "local", type == "preorder", free, LegacyValues.Time(row, "created_at"));
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or DivideByZeroException) { throw new MigrationRejectedException(); }
    }
    public IReadOnlyList<LegacyMapProof> Proofs(BrandContext context, SecretSnapshot secrets)
    {
        _ = Terms(); var proof = Record.Source.Proof(Record.Target, context, secrets);
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes, Encoding.UTF8, true);
        writer.Write("legacy-record-order-v1"); writer.Write(proof.Fingerprint.Span); writer.Write(Member.ToByteArray()); writer.Write(HistoricalBrandName); writer.Write(Record.Reference); writer.Write(Record.Digest); writer.Flush();
        return [proof, new(LegacyProjection.HistoricalOrder, Record.Source.Id, Record.Target, context.BrandId, SHA256.HashData(bytes.ToArray())), new(LegacyProjection.OrderAlias, Record.Source.Id, Record.Target, context.BrandId, proof.Fingerprint.Span)];
    }
    public override string ToString() => "LegacyHistoricalRecord [redacted]";
}
internal sealed record LegacyOrderTerms(long Telegram, int Quantity, string Title, decimal UnitUsd, decimal? UnitToman, string Method, string Currency, string Kind, bool Preorder, bool Free, DateTime Created);
public sealed class LegacyOrderRecordImportWorkflow(DbSessionFactory sessions, SecretSnapshot secrets)
{
    public async Task ImportAsync(BrandContext context, VerifiedImportManifest manifest, LegacyHistoricalRecord row, CancellationToken ct = default)
    {
        var terms = row.Terms(); var proofs = row.Proofs(context, secrets);
        if (context.BrandId != manifest.TargetBrand || !new LegacyRecordArchive(secrets).Matches(context, row.Record, row.Record.Evidence)) throw new MigrationRejectedException();
        foreach (var proof in proofs) manifest.Require(proof);
        await using var session = await sessions.BeginAsync(ct); await LegacyMapStore.RequireDestinationAsync(session, manifest, ct);
        await LegacyValues.RequireMemberAsync(session, context, manifest, row.Member, terms.Telegram, ct);
        if (terms.Method == "wallet" && !await session.QuerySingleAsync(new DbStatement("SELECT EXISTS(SELECT 1 FROM shop.wallet_entries w JOIN shop.legacy_id_map m ON m.target_kind='wallet_entry' AND m.target_id=w.id WHERE m.source_brand=$1 AND m.source_table='transactions' AND m.legacy_id=$2 AND m.import_batch=$3 AND m.import_manifest_ref=$4 AND w.user_id=$5 AND NOT w.is_posting AND w.movement_kind='legacy_history' AND w.signed_amount=$6)", DbValue.Text(manifest.SourceBrand), DbValue.Text(row.Record.Source.Id), DbValue.Uuid(manifest.Batch), DbValue.Text(manifest.Reference), DbValue.Uuid(row.Member), DbValue.Numeric(-terms.UnitUsd * terms.Quantity)), r => r.GetBoolean(0), ct)) throw new MigrationRejectedException();
        if (terms.Free && !manifest.Proofs.Any(p => p.Projection == LegacyProjection.ZeroTransaction && p.LegacyId == row.Record.Source.Id && p.Target == row.Record.Target)) throw new MigrationRejectedException();
        bool first = await LegacyMapStore.EnsureAsync(session, manifest, proofs[0], ct);
        foreach (var proof in proofs.Skip(1)) if (await LegacyMapStore.EnsureAsync(session, manifest, proof, ct) != first) throw new MigrationRejectedException();
        if (first)
        {
            await session.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.orders(id,user_id,quantity,order_kind,payment_status,fulfillment_status,currency,payment_method,unit_price_usdt,unit_price_toman,
                    auto_price_toman_snapshot,pricing_rounding_step_snapshot,price_calculation_version,discount_pct_snapshot,discount_units_snapshot,payment_confirmed_at,
                    brand_public_name_snapshot,product_title_snapshot,payment_terms_snapshot,fulfillment_kind_snapshot,local_first_snapshot,is_preorder,review_code,created_at,updated_at)
                VALUES($1,$2,$3,'product_purchase',$4,'requires_admin',$5,$6,$7,$8,false,5000,'legacy-net-v1',$9,$10,$11,$12,$13,$14,$15,false,$16,'credential_recovery',$11,$11)
                """, DbValue.Uuid(row.Record.Target), DbValue.Uuid(row.Member), DbValue.Integer(terms.Quantity), DbValue.Text(terms.Free ? "not_required" : "paid"), DbValue.Text(terms.Currency), DbValue.Text(terms.Method), DbValue.Numeric(terms.UnitUsd),
                terms.UnitToman is decimal toman ? DbValue.Numeric(toman) : DbValue.Null(NpgsqlDbType.Numeric), terms.Free ? DbValue.Numeric(100) : DbValue.Null(NpgsqlDbType.Numeric), terms.Free ? DbValue.Integer(terms.Quantity) : DbValue.Null(NpgsqlDbType.Integer),
                DbValue.Utc(terms.Created), DbValue.Text(row.HistoricalBrandName), DbValue.Text(terms.Title), DbValue.Text(row.Record.Reference), DbValue.Text(terms.Kind), DbValue.Boolean(terms.Preorder)), ct);
            await LegacyValues.AuditAsync(session, manifest, row.Record, "order", ct);
        }
        if (!await MatchesAsync(session, row, ct) || !await LegacyValues.MatchesAuditAsync(session, manifest, row.Record, "order", ct)) throw new MigrationRejectedException();
        await session.CommitAsync(ct);
    }
    internal static Task<bool> MatchesAsync(DbSession session, LegacyHistoricalRecord row, CancellationToken ct)
    {
        var t = row.Terms();
        return session.QuerySingleAsync(new DbStatement("""
            SELECT EXISTS(SELECT 1 FROM shop.orders WHERE id=$1 AND user_id=$2 AND quantity=$3 AND product_id IS NULL AND order_kind='product_purchase'
                AND payment_status=$4 AND fulfillment_status='requires_admin' AND currency=$5 AND payment_method=$6 AND unit_price_usdt=$7 AND unit_price_toman IS NOT DISTINCT FROM $8
                AND NOT auto_price_toman_snapshot AND pricing_rounding_step_snapshot=5000 AND price_calculation_version='legacy-net-v1'
                AND discount_pct_snapshot IS NOT DISTINCT FROM $9 AND discount_units_snapshot IS NOT DISTINCT FROM $10 AND payment_confirmed_at=$11 AND created_at=$11 AND updated_at=$11
                AND brand_public_name_snapshot=$12 AND product_title_snapshot=$13 AND payment_terms_snapshot=$14 AND fulfillment_kind_snapshot=$15 AND NOT local_first_snapshot AND is_preorder=$16
                AND review_code='credential_recovery' AND release_authorized_at IS NULL AND delivery_generation IS NULL AND resolution_kind IS NULL
                AND fx_rate_snapshot IS NULL AND payment_due_at IS NULL AND hold_at IS NULL AND product_description_snapshot IS NULL AND product_icon_snapshot IS NULL AND product_guidance_snapshot IS NULL AND NOT EXISTS(SELECT 1 FROM shop.fulfillments WHERE order_id=$1)
                AND NOT EXISTS(SELECT 1 FROM shop.supplier_requests WHERE order_id=$1) AND NOT EXISTS(SELECT 1 FROM shop.wallet_entries WHERE order_id=$1))
            """, DbValue.Uuid(row.Record.Target), DbValue.Uuid(row.Member), DbValue.Integer(t.Quantity), DbValue.Text(t.Free ? "not_required" : "paid"), DbValue.Text(t.Currency), DbValue.Text(t.Method), DbValue.Numeric(t.UnitUsd),
            t.UnitToman is decimal toman ? DbValue.Numeric(toman) : DbValue.Null(NpgsqlDbType.Numeric), t.Free ? DbValue.Numeric(100) : DbValue.Null(NpgsqlDbType.Numeric), t.Free ? DbValue.Integer(t.Quantity) : DbValue.Null(NpgsqlDbType.Integer),
            DbValue.Utc(t.Created), DbValue.Text(row.HistoricalBrandName), DbValue.Text(t.Title), DbValue.Text(row.Record.Reference), DbValue.Text(t.Kind), DbValue.Boolean(t.Preorder)), r => r.GetBoolean(0), ct);
    }
}
