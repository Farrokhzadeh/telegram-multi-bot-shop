using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Entities;
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
            !await session.RunEfStepAsync(false, (data, token) => (from o in data.Data.Set<Order>() join u in data.Data.Set<User>() on o.UserId equals u.Id where o.Id == paid && o.OrderKind == "prepaid_units" && o.PaymentStatus == "paid" && o.UserId == row.Member && o.ProductId == row.Product && o.Quantity == terms.Maximum && u.BrandId == context.BrandId && data.Data.Set<Payment>().Any(p => p.OrderId == o.Id && p.Status == "settled" && p.LegacyKind != null && p.EvidenceAssetRef != null && p.EvidenceDigest != null) select o.Id).AnyAsync(token), ct))) throw new MigrationRejectedException();
        bool first = await LegacyMapStore.EnsureAsync(session, manifest, proofs[0], ct);
        foreach (var proof in proofs.Skip(1)) if (await LegacyMapStore.EnsureAsync(session, manifest, proof, ct) != first) throw new MigrationRejectedException();
        if (first)
        {
            string key = row.Record.Evidence.KeyId; byte[] plain = Encoding.UTF8.GetBytes(terms.Code), encrypted;
            try { encrypted = new BrandCipher(secrets).Encrypt(context, "discounts", row.Record.Target, "code_ciphertext", plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            if (key != secrets.CurrentBrandKeyId(context)) throw new MigrationRejectedException();
            byte[] digest = Digest(context, key, terms.Code);
            await session.RunEfStepAsync(true, (data, token) =>
            {
                data.RequireImportCommand(); Guid audit = LegacyValues.AuditId(manifest, row.Record);
                data.Data.Add(new Discount { Id = row.Record.Target, BrandId = context.BrandId, CodeDigest = digest, CodeCiphertext = encrypted, CodeKeyId = key, DisplayPrefix = terms.Code[..Math.Min(3, terms.Code.Length)], IssuanceBatchId = manifest.Batch, Kind = row.Kind, PercentOff = terms.Percent, ProductId = row.Product, EligibleUserId = row.Member, SourceOrderId = row.PaidSourceOrder, MaxTotalUses = terms.Maximum, UsageAdjustmentUnits = terms.Used, Active = false });
                data.Data.Add(new AuditEvent { Id = audit, ActorKind = "system", BrandId = context.BrandId, Action = "legacy_import", EntityKind = "discount", EntityId = row.Record.Target, Reason = row.Record.Reference, CommandSha256 = row.Record.Digest, CorrelationId = manifest.Batch, NewRevision = 0 });
                data.Data.Add(new DiscountRevision { Id = row.Revision, DiscountId = row.Record.Target, RevisionNo = 0, AuditEventId = audit, PercentOff = terms.Percent, MaxTotalUses = terms.Maximum, UsageAdjustmentUnits = terms.Used, RedeemedCountAtRevision = 0, HeldUnitsAtRevision = 0, Active = false, ProductId = row.Product, EligibleUserId = row.Member }); return Task.FromResult(true);
            }, ct);
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
        byte[] digest = Digest(context, row.Record.Evidence.KeyId, t.Code); Guid audit = LegacyValues.AuditId(manifest, row.Record);
        bool fields = await session.RunEfStepAsync(false, (data, token) => (from d in data.Data.Set<Discount>() join r in data.Data.Set<DiscountRevision>() on d.Id equals r.DiscountId where d.Id == row.Record.Target && d.BrandId == context.BrandId && d.CodeDigest == digest && d.CodeKeyId == row.Record.Evidence.KeyId && d.IssuanceBatchId == manifest.Batch && d.Kind == row.Kind && d.PercentOff == t.Percent && d.ProductId == row.Product && d.EligibleUserId == row.Member && d.SourceOrderId == row.PaidSourceOrder && d.MaxTotalUses == t.Maximum && d.RedeemedCount == 0 && d.UsageAdjustmentUnits == t.Used && !d.Active && d.Revision == 0 && d.CreatedByAdminId == null && d.MaxUsesPerUser == null && d.StartsAt == null && d.EndsAt == null && r.Id == row.Revision && r.RevisionNo == 0 && r.AuditEventId == audit && r.PercentOff == d.PercentOff && r.MaxTotalUses == d.MaxTotalUses && r.UsageAdjustmentUnits == d.UsageAdjustmentUnits && r.RedeemedCountAtRevision == 0 && r.HeldUnitsAtRevision == 0 && r.Active == d.Active && r.ProductId == d.ProductId && r.EligibleUserId == d.EligibleUserId && !data.Data.Set<DiscountUse>().Any(use => use.DiscountId == d.Id) select d.Id).AnyAsync(token), ct);
        if (!fields) return false;
        byte[] encrypted = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Discount>().Where(value => value.Id == row.Record.Target).Select(row => row.CodeCiphertext).SingleAsync(token), ct);
        byte[] plain = new BrandCipher(secrets).Decrypt(context, "discounts", row.Record.Target, "code_ciphertext", row.Record.Evidence.KeyId, encrypted), expected = Encoding.UTF8.GetBytes(t.Code);
        try { return CryptographicOperations.FixedTimeEquals(plain, expected); }
        finally { CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(expected); }
    }
}
