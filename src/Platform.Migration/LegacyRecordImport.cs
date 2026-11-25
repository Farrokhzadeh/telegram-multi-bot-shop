using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Entities;
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
            !await session.RunEfStepAsync(false, (data, token) => (from u in data.Data.Set<User>() join b in data.Data.Set<Brand>() on u.BrandId equals b.Id where u.Id == member && u.TelegramId == telegram && b.Id == context.BrandId && b.BotId == context.BotId select u.Id).AnyAsync(token), ct)) throw new MigrationRejectedException();
    }
    internal static Task<bool> HasHistoricalDebitAsync(DbSession session, VerifiedImportManifest manifest, string legacyOrder, Guid member, decimal amount, CancellationToken ct, DateTime? occurred = null) => session.RunEfStepAsync(false, (data, token) => (from w in data.Data.Set<WalletEntry>() join m in data.Data.Set<LegacyIdMap>() on w.Id equals m.TargetId where m.TargetKind == "wallet_entry" && m.SourceBrand == manifest.SourceBrand && m.SourceTable == "transactions" && m.LegacyId == legacyOrder && m.ImportBatch == manifest.Batch && m.ImportManifestRef == manifest.Reference && w.UserId == member && !w.IsPosting && w.MovementKind == "legacy_history" && w.SignedAmount == -amount && (occurred == null || w.CreatedAt == occurred) select w.Id).AnyAsync(token), ct);
    internal static async Task AuditAsync(DbSession session, VerifiedImportManifest manifest, ArchivedLegacyRecord row, string kind, CancellationToken ct) => await session.RunEfStepAsync(true, (data, token) => { data.RequireImportCommand(); data.Data.Add(new AuditEvent { Id = AuditId(manifest, row), ActorKind = "system", BrandId = manifest.TargetBrand, Action = "legacy_import", EntityKind = kind, EntityId = row.Target, Reason = row.Reference, CommandSha256 = row.Digest, CorrelationId = manifest.Batch }); return Task.FromResult(true); }, ct);
    internal static Task<bool> MatchesAuditAsync(DbSession session, VerifiedImportManifest manifest, ArchivedLegacyRecord row, string kind, CancellationToken ct) => session.RunEfStepAsync(false, (data, token) => data.Data.Set<AuditEvent>().AnyAsync(a => a.Id == AuditId(manifest, row) && a.ActorKind == "system" && a.BrandId == manifest.TargetBrand && a.Action == "legacy_import" && a.EntityKind == kind && a.EntityId == row.Target && a.Reason == row.Reference && a.CommandSha256 == row.Digest && a.CorrelationId == manifest.Batch, token), ct);

}
public sealed record LegacyHistoricalRecord(ArchivedLegacyRecord Record, Guid Member, string HistoricalBrandName, DateTime? NativeWalletDebitAt = null, LegacySourceRecord? NativeZeroSource = null, DateTime? NativePaymentSettledAt = null)
{
    internal LegacyOrderTerms Terms()
    {
        if (Record.Source.Projection != LegacyProjection.OrderRecord || Member == Guid.Empty || string.IsNullOrWhiteSpace(HistoricalBrandName) || HistoricalBrandName.Length > 160) throw new MigrationRejectedException();
        var terms = ReadTerms(Record.Source);
        if (NativeWalletDebitAt is DateTime clock && (terms.Method != "wallet" || terms.Free || clock.Kind != DateTimeKind.Utc || clock == DateTime.MinValue || clock == DateTime.MaxValue || clock.Ticks % 10 != 0)) throw new MigrationRejectedException();
        if (NativePaymentSettledAt is DateTime paid && (terms.Free || terms.Method is not ("bank" or "crypto") || NativeWalletDebitAt is not null || NativeZeroSource is not null || paid.Kind != DateTimeKind.Utc || paid == DateTime.MinValue || paid == DateTime.MaxValue || paid.Ticks % 10 != 0 || paid < terms.Created)) throw new MigrationRejectedException();
        if (NativeZeroSource is not null)
        {
            using var zero = NativeZeroSource.Open(); var original = zero.RootElement;
            if (!terms.Free || NativeWalletDebitAt is not null || NativeZeroSource.Projection != LegacyProjection.ZeroTransaction || NativeZeroSource.Id != Record.Source.Id || original.GetProperty("user_id").GetInt64() != terms.Telegram || LegacyValues.Text(original, "currency") != "USD") throw new MigrationRejectedException();
            _ = LegacyValues.Time(original, "created_at");
        }
        return terms;
    }
    internal DateTime AcceptanceClock(LegacyOrderTerms terms)
    { if (NativeZeroSource is null) return NativePaymentSettledAt ?? NativeWalletDebitAt ?? terms.Created; using var zero = NativeZeroSource.Open(); return LegacyValues.Time(zero.RootElement, "created_at"); }
    internal static LegacyOrderTerms ReadTerms(LegacySourceRecord source)
    {
        if (source.Projection != LegacyProjection.OrderRecord) throw new MigrationRejectedException();
        try
        {
            using var document = source.Open(); var row = document.RootElement; var snapshot = row.GetProperty("checkout_snapshot");
            int quantity = row.GetProperty("qty").GetInt32(); long product = row.GetProperty("product_id").GetInt64(); decimal total = LegacyValues.Number(row, "total");
            string currency = LegacyValues.Text(row, "currency"), method = LegacyValues.Text(snapshot, "payment_method"), type = LegacyValues.Text(snapshot, "product_type"), title = LegacyValues.Text(snapshot, "product_name");
            decimal original = LegacyValues.Number(snapshot, "original_usd"), net = LegacyValues.Number(snapshot, "total_usd"), originalToman = LegacyValues.Number(snapshot, "original_toman"), netToman = LegacyValues.Number(snapshot, "total_toman");
            int discounted = snapshot.GetProperty("discount_quantity").GetInt32(); string? code = LegacyValues.Optional(snapshot, "discount_code"), percent = LegacyValues.Optional(snapshot, "discount_pct");
            if (source.Id.Length != 16 || !source.Id.StartsWith("ORD-", StringComparison.Ordinal) || source.Id.Skip(4).Any(c => !char.IsAsciiHexDigitUpper(c)) ||
                quantity <= 0 || product <= 0 || snapshot.GetProperty("version").GetInt32() != 1 || snapshot.GetProperty("product_id").GetInt64() != product || snapshot.GetProperty("quantity").GetInt32() != quantity ||
                LegacyValues.Text(snapshot, "currency") != currency || string.IsNullOrWhiteSpace(title) || title.Length > 160 || original <= 0 || net < 0 || net > original || originalToman < 0 || netToman < 0 || netToman > originalToman ||
                LegacyValues.Number(snapshot, "savings_usd") != ExactDecimal.Subtract(original, net) || LegacyValues.Number(snapshot, "savings_toman") != ExactDecimal.Subtract(originalToman, netToman) || ExactDecimal.Multiply(ExactDecimal.Divide(original, quantity), quantity) != original ||
                type is not ("credential" or "service" or "api" or "preorder") || method is not ("wallet" or "crypto" or "bank") || (method == "bank" ? currency != "IRR" || total != netToman || netToman <= 0 : currency != "USD" || total != net)) throw new MigrationRejectedException();
            if (code is null) { if (percent is not null || discounted != 0 || net != original || netToman != originalToman) throw new MigrationRejectedException(); }
            else
            {
                decimal pct = ExactDecimal.Parse(percent ?? throw new MigrationRejectedException());
                if (string.IsNullOrWhiteSpace(code) || code.Length > 64 || pct is <= 0 or > 100 || discounted < 1 || discounted > quantity || net != decimal.Round(ExactDecimal.Subtract(original, ExactDecimal.Divide(ExactDecimal.Multiply(ExactDecimal.Multiply(ExactDecimal.Divide(original, quantity), discounted), pct), 100)), 2, MidpointRounding.ToEven)) throw new MigrationRejectedException();
            }
            bool free = net == 0;
            if ((free && (method == "bank" || netToman != 0 || discounted != quantity || percent is null || ExactDecimal.Parse(percent) != 100)) || (!free && ExactDecimal.Multiply(ExactDecimal.Divide(net, quantity), quantity) != net) || (method == "bank" && ExactDecimal.Multiply(ExactDecimal.Divide(netToman, quantity), quantity) != netToman)) throw new MigrationRejectedException();
            return new(row.GetProperty("user_id").GetInt64(), quantity, title, free ? ExactDecimal.Divide(original, quantity) : ExactDecimal.Divide(net, quantity), method == "bank" ? ExactDecimal.Divide(netToman, quantity) : null,
                free ? "no_payment_required" : method, method == "bank" ? "TOMAN" : "USDT", type == "service" ? "manual" : type == "api" ? "supplier" : "local", type == "preorder", free, LegacyValues.Time(row, "created_at"));
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArithmeticException) { throw new MigrationRejectedException(); }
    }
    internal static string ReviewReason(LegacySourceRecord source)
    {
        using var document = source.Open(); var row = document.RootElement;
        if (!row.TryGetProperty("checkout_snapshot", out var snapshot) || snapshot.ValueKind != JsonValueKind.Object || !snapshot.EnumerateObject().Any()) return "missing_checkout_snapshot";
        try
        {
            string method = LegacyValues.Text(snapshot, "payment_method");
            if (method == "wallet" && LegacyValues.Text(row, "currency") == "IRR") return "original_wallet_irr_terms";
            int quantity = row.GetProperty("qty").GetInt32();
            if (quantity > 0)
            {
                _ = ExactDecimal.Divide(LegacyValues.Number(snapshot, "original_usd"), quantity);
                if (method == "bank") _ = ExactDecimal.Divide(LegacyValues.Number(snapshot, "total_toman"), quantity);
                else _ = ExactDecimal.Divide(LegacyValues.Number(snapshot, "total_usd"), quantity);
            }
        }
        catch (ArithmeticException error) when (error is not OverflowException) { return "nonterminating_unit_terms"; }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or MigrationRejectedException) { }
        return "incomplete_order_terms";
    }
    public IReadOnlyList<LegacyMapProof> Proofs(BrandContext context, SecretSnapshot secrets)
    {
        _ = Terms(); var proof = Record.Source.Proof(Record.Target, context, secrets);
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes, Encoding.UTF8, true);
        writer.Write("legacy-record-order-v1"); writer.Write(proof.Fingerprint.Span); writer.Write(Member.ToByteArray()); writer.Write(HistoricalBrandName); writer.Write(Record.Reference); writer.Write(Record.Digest); if (NativeWalletDebitAt is DateTime clock) { writer.Write("native-wallet-debit-clock-v1"); writer.Write(clock.Ticks); }
        if (NativePaymentSettledAt is DateTime paid) { writer.Write("native-settled-payment-clock-v1"); writer.Write(paid.Ticks); }
        if (NativeZeroSource is not null) { writer.Write("native-zero-acceptance-v1"); writer.Write(NativeZeroSource.Proof(Record.Target, context, secrets).Fingerprint.Span); }
        writer.Flush();
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
        if (row.NativeZeroSource is not null) manifest.Require(row.NativeZeroSource.Proof(row.Record.Target, context, secrets));
        await using var session = await sessions.BeginAsync(ct); await LegacyMapStore.RequireDestinationAsync(session, manifest, ct);
        await LegacyValues.RequireMemberAsync(session, context, manifest, row.Member, terms.Telegram, ct);
        if (terms.Method == "wallet" && !await LegacyValues.HasHistoricalDebitAsync(session, manifest, row.Record.Source.Id, row.Member, ExactDecimal.Multiply(terms.UnitUsd, terms.Quantity), ct, row.NativeWalletDebitAt)) throw new MigrationRejectedException();
        if (terms.Free && !manifest.Proofs.Any(p => p.Projection == LegacyProjection.ZeroTransaction && p.LegacyId == row.Record.Source.Id && p.Target == row.Record.Target)) throw new MigrationRejectedException();
        bool first = await LegacyMapStore.EnsureAsync(session, manifest, proofs[0], ct);
        foreach (var proof in proofs.Skip(1)) if (await LegacyMapStore.EnsureAsync(session, manifest, proof, ct) != first) throw new MigrationRejectedException();
        if (first)
        {
            await session.RunEfStepAsync(true, (data, token) => { data.RequireImportCommand(); data.Data.Add(new Order { Id = row.Record.Target, UserId = row.Member, Quantity = terms.Quantity, OrderKind = "product_purchase", PaymentStatus = terms.Free ? "not_required" : "paid", FulfillmentStatus = "requires_admin", Currency = terms.Currency, PaymentMethod = terms.Method, UnitPriceUsdt = terms.UnitUsd, UnitPriceToman = terms.UnitToman, AutoPriceTomanSnapshot = false, PricingRoundingStepSnapshot = 5000m, PriceCalculationVersion = "legacy-net-v1", DiscountPctSnapshot = terms.Free ? 100m : null, DiscountUnitsSnapshot = terms.Free ? terms.Quantity : null, PaymentConfirmedAt = row.AcceptanceClock(terms), BrandPublicNameSnapshot = row.HistoricalBrandName, ProductTitleSnapshot = terms.Title, PaymentTermsSnapshot = row.Record.Reference, FulfillmentKindSnapshot = terms.Kind, LocalFirstSnapshot = false, IsPreorder = terms.Preorder, ReviewCode = "credential_recovery", CreatedAt = terms.Created, UpdatedAt = terms.Created }); return Task.FromResult(true); }, ct);
            await LegacyValues.AuditAsync(session, manifest, row.Record, "order", ct);
        }
        if (!await MatchesAsync(session, row, ct) || !await LegacyValues.MatchesAuditAsync(session, manifest, row.Record, "order", ct)) throw new MigrationRejectedException();
        await session.CommitAsync(ct);
    }
    internal static Task<bool> MatchesAsync(DbSession session, LegacyHistoricalRecord row, CancellationToken ct)
    {
        var t = row.Terms();
        DateTime accepted = row.AcceptanceClock(t); decimal? percent = t.Free ? 100m : null; int? units = t.Free ? t.Quantity : null;
        return session.RunEfStepAsync(false, (data, token) => data.Data.Set<Order>().AnyAsync(o => o.Id == row.Record.Target && o.UserId == row.Member && o.Quantity == t.Quantity && o.ProductId == null && o.OrderKind == "product_purchase" && o.PaymentStatus == (t.Free ? "not_required" : "paid") && o.FulfillmentStatus == "requires_admin" && o.Currency == t.Currency && o.PaymentMethod == t.Method && o.UnitPriceUsdt == t.UnitUsd && o.UnitPriceToman == t.UnitToman && !o.AutoPriceTomanSnapshot && o.PricingRoundingStepSnapshot == 5000m && o.PriceCalculationVersion == "legacy-net-v1" && o.DiscountPctSnapshot == percent && o.DiscountUnitsSnapshot == units && o.PaymentConfirmedAt == accepted && o.CreatedAt == t.Created && o.UpdatedAt == t.Created && o.BrandPublicNameSnapshot == row.HistoricalBrandName && o.ProductTitleSnapshot == t.Title && o.PaymentTermsSnapshot == row.Record.Reference && o.FulfillmentKindSnapshot == t.Kind && !o.LocalFirstSnapshot && o.IsPreorder == t.Preorder && o.ReviewCode == "credential_recovery" && o.ReleaseAuthorizedAt == null && o.DeliveryGeneration == null && o.ResolutionKind == null && o.FxRateSnapshot == null && o.PaymentDueAt == null && o.HoldAt == null && o.ProductDescriptionSnapshot == null && o.ProductIconSnapshot == null && o.ProductGuidanceSnapshot == null && !data.Data.Set<Fulfillment>().Any(f => f.OrderId == o.Id) && !data.Data.Set<SupplierRequest>().Any(r => r.OrderId == o.Id) && !data.Data.Set<WalletEntry>().Any(w => w.OrderId == o.Id), token), ct);
    }
}
