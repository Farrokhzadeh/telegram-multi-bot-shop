using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NpgsqlTypes;
using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Configurations;
using Platform.Core;

namespace Platform.Persistence;

public enum CouponKind { Promotion, Goodwill, Prepaid }
public enum CouponLookupOutcome { Found, NotFound, RateLimited }
public sealed record CouponLookup(CouponLookupOutcome Outcome, Guid? DiscountId = null);
public sealed record CouponTerms(Guid Id, string Code, CouponKind Kind, decimal Percent, Guid? ProductId = null, Guid? MemberId = null,
    Guid? SourceOrderId = null, long? MaximumUses = null, long? MaximumPerMember = null, DateTime? StartsAt = null, DateTime? EndsAt = null, Guid? BatchId = null)
{
    public override string ToString() => "CouponTerms [redacted]";
}
public sealed record CouponClaim(Guid DiscountId, Guid OrderId, Guid RevisionId, int Units, Money Saved, string Status);

public sealed partial class PricingStore(DbSessionFactory sessions)
{
    public async Task<Guid> IssueAsync(DbSession session, BrandContext context, AuthenticatedAdmin actor, CouponTerms terms, Guid commandId, CancellationToken ct = default)
    {
        try { return await IssueCoreAsync(session, context, actor, terms, commandId, ct); }
        catch { Poison(session); throw; }
    }

    private async Task<Guid> IssueCoreAsync(DbSession session, BrandContext context, AuthenticatedAdmin actor, CouponTerms terms, Guid commandId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(terms);
        string code = Normalize(terms.Code);
        if (terms.Id == Guid.Empty || terms.ProductId == Guid.Empty || terms.MemberId == Guid.Empty || terms.SourceOrderId == Guid.Empty || terms.BatchId == Guid.Empty ||
            !Enum.IsDefined(terms.Kind) || terms.Percent is < 0 or > 100 || decimal.Round(terms.Percent, 2) != terms.Percent || terms.MaximumUses < 0 || terms.MaximumPerMember < 0 ||
            terms.StartsAt?.Kind is DateTimeKind.Local or DateTimeKind.Unspecified || terms.EndsAt?.Kind is DateTimeKind.Local or DateTimeKind.Unspecified || terms.EndsAt <= terms.StartsAt ||
            (terms.Kind == CouponKind.Prepaid ? terms.ProductId is null || terms.MemberId is null || terms.SourceOrderId is null || terms.Percent != 100 || terms.MaximumUses is not > 0 || terms.BatchId is not null : terms.SourceOrderId is not null))
            throw new PricingRejectedException();
        await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
        byte[] digest;
        byte[] codeBytes = Encoding.UTF8.GetBytes(code);
        try
        {
            digest = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.Discount>()
                .Where(value => value.Id == terms.Id && value.BrandId == context.BrandId && value.CodeContent == codeBytes)
                .Select(value => value.CodeDigest).SingleOrDefaultAsync(token), ct) ?? Digest(context, code);
        }
        finally { CryptographicOperations.ZeroMemory(codeBytes); }
        var command = new AuditCommand(commandId, AuditAction.CouponIssue, AuditEntity.Discount, terms.Id, context.BrandId, AuditReason.OperatorReview, Guid.NewGuid(), Commitment(terms, digest), newRevision: 0);
        if (!await AdminAuditStore.AppendAsync(session, actor, command, ct)) return terms.Id;
        await session.RunEfStepAsync(false, async (data, token) =>
        {
            if (terms.MemberId is Guid member) await data.LockUserAsync(member, context.BrandId, token);
            if (terms.ProductId is Guid product) await data.LockProductAsync(product, token);
            return true;
        }, ct);
        await LockBrandAsync(session, context, ct);
        byte[] plain = Encoding.UTF8.GetBytes(code);
        try
        {
            await session.RunEfStepAsync(true, async (data, token) =>
            {
                await data.RequireAdminAsync(actor, null, token);
                await data.RequireBrandCommandAsync(context.BrandId, token);
                if (await data.Data.Set<Entities.Discount>().AnyAsync(row => row.BrandId == context.BrandId && row.CodeContent == plain, token))
                    data.RejectConflict("Duplicate coupon code.");
                data.Data.Add(new Entities.Discount { Id = terms.Id, BrandId = context.BrandId, CodeDigest = digest, CodeContent = plain, DisplayPrefix = code[..Math.Min(3, code.Length)], IssuanceBatchId = terms.BatchId ?? terms.Id, Kind = terms.Kind switch { CouponKind.Promotion => "promotion", CouponKind.Goodwill => "goodwill", CouponKind.Prepaid => "prepaid_entitlement", _ => throw new PricingRejectedException() }, PercentOff = terms.Percent, ProductId = terms.ProductId, EligibleUserId = terms.MemberId, SourceOrderId = terms.SourceOrderId, MaxTotalUses = terms.MaximumUses, MaxUsesPerUser = terms.MaximumPerMember, StartsAt = terms.StartsAt, EndsAt = terms.EndsAt, CreatedByAdminId = actor.AdminId });
                await data.FlushAsync(token, false);
                data.Data.Add(new Entities.DiscountRevision { Id = Guid.NewGuid(), DiscountId = terms.Id, RevisionNo = 0, AuditEventId = commandId, PercentOff = terms.Percent, MaxTotalUses = terms.MaximumUses, MaxUsesPerUser = terms.MaximumPerMember, StartsAt = terms.StartsAt, EndsAt = terms.EndsAt, Active = true, ProductId = terms.ProductId, EligibleUserId = terms.MemberId });
                return true;
            }, ct);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
        if (terms.MemberId is not null) await OutboxStore.EnqueueAsync(session, JobIntent.Coupon(context.BrandId, terms.Id), terms.Id, DateTime.UtcNow, ct);
        return terms.Id;
    }

    public async Task<CouponLookup> LookupAsync(BrandContext context, long verifiedActorId, string suppliedCode, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginAsync(ct);
        BrandUser member = await RequireCustomerAsync(session, context, verifiedActorId, ct);
        var result = await session.RunEfStepAsync(true, async (data, token) =>
        {
            await data.RequireMembershipAsync(context, verifiedActorId, false, false, token);
            long attempts = await data.Data.Set<Entities.BotActivityEvent>().LongCountAsync(value => value.UserId == member.Id && value.IdempotencyKey.StartsWith("coupon_lookup:") && value.OccurredAt > EfValues.UtcNow().AddMinutes(-1), token);
            if (attempts >= 20) return new CouponLookup(CouponLookupOutcome.RateLimited);
            Guid? found = null;
            string? code = null;
            try { code = Normalize(suppliedCode); } catch (PricingRejectedException) { }
            if (code is not null)
            {
                var eligible = CustomerCoupons(data, context, member.Id).Where(value => value.Active &&
                    (value.StartsAt == null || value.StartsAt <= EfValues.UtcNow()) && (value.EndsAt == null || value.EndsAt > EfValues.UtcNow()));
                byte[] content = Encoding.UTF8.GetBytes(code);
                try { found = await eligible.Where(value => value.CodeContent == content).Select(value => (Guid?)value.Id).SingleOrDefaultAsync(token); }
                finally { CryptographicOperations.ZeroMemory(content); }
            }
            data.Data.Add(new Entities.BotActivityEvent { Id = Guid.NewGuid(), UserId = member.Id, EventType = "discount", SourceKind = "flow", ActionCode = "discounts_open", OutcomeCode = found is null ? "rejected" : "accepted", IdempotencyKey = "coupon_lookup:" + Guid.NewGuid().ToString("N") });
            return new CouponLookup(found is null ? CouponLookupOutcome.NotFound : CouponLookupOutcome.Found, found);
        }, ct);
        await session.CommitAsync(ct);
        return result;
    }

    public async Task<string> ReadCodeAsync(DbSession session, BrandContext context, long verifiedActorId, Guid discountId, CancellationToken ct = default)
    {
        BrandUser member = await RequireCustomerAsync(session, context, verifiedActorId, ct);
        var value = await session.RunEfStepAsync(false, (data, token) =>
        {
            var rows = CustomerCoupons(data, context, member.Id).Where(value => value.Id == discountId);
            return rows.Select(value => new StoredCode(value.CodeContent)).SingleOrDefaultAsync(token);
        }, ct);
        if (value is null) Reject(session);
        try { return Encoding.UTF8.GetString(value.Content); }
        finally { CryptographicOperations.ZeroMemory(value.Content); }
    }

    public async Task<CouponClaim> HoldAsync(DbSession session, BrandContext context, long verifiedActorId, Guid discountId, Guid orderId, CancellationToken ct = default)
    {
        try { return await HoldCoreAsync(session, context, verifiedActorId, discountId, orderId, ct); }
        catch { Poison(session); throw; }
    }

    private static async Task<CouponClaim> HoldCoreAsync(DbSession session, BrandContext context, long verifiedActorId, Guid discountId, Guid orderId, CancellationToken ct)
    {
        BrandUser member = await RequireCustomerAsync(session, context, verifiedActorId, ct);
        await LockBrandAsync(session, context, ct);
        var order = await session.RunEfStepAsync(false, async (data, token) =>
        {
            await data.LockDiscountAsync(discountId, context.BrandId, token);
            await data.LockOrderAsync(orderId, token);
            var row = await data.Data.Set<Entities.Order>().Where(value => value.Id == orderId && value.UserId == member.Id).Select(value => new { value.Quantity, value.Currency, value.UnitPriceUsdt, value.UnitPriceToman, value.AutoPriceTomanSnapshot, value.FxRateSnapshot, value.FxSourceSnapshot, value.FxFetchedAtSnapshot, value.DiscountPctSnapshot, value.DiscountUnitsSnapshot, value.PriceCalculationVersion, value.PaymentStatus, value.PricingRoundingStepSnapshot }).SingleOrDefaultAsync(token);
            return row is null ? null : new ClaimOrder(row.Quantity, row.Currency, row.UnitPriceUsdt, row.UnitPriceToman, row.AutoPriceTomanSnapshot, row.FxRateSnapshot is decimal rate ? new(rate, row.FxSourceSnapshot!, row.FxFetchedAtSnapshot!.Value) : null, row.DiscountPctSnapshot, row.DiscountUnitsSnapshot, row.PriceCalculationVersion, row.PaymentStatus, row.PricingRoundingStepSnapshot);
        }, ct);
        if (order is null) Reject(session);
        CouponClaim? existing = await ReadClaimAsync(session, discountId, orderId, ct);
        if (existing is not null)
        {
            if (existing.Status == "released") Reject(session);
            return existing;
        }
        bool otherClaim = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.DiscountUse>().AnyAsync(value => value.OrderId == orderId && value.DiscountId != discountId, token), ct);
        if (otherClaim) Reject(session);
        var grant = await session.RunEfStepAsync(false, (data, token) => (from discount in data.Data.Set<Entities.Discount>()
                                                                          join revision in data.Data.Set<Entities.DiscountRevision>() on new { Id = discount.Id, Revision = discount.Revision } equals new { Id = revision.DiscountId, Revision = revision.RevisionNo }
                                                                          where discount.Id == discountId && discount.BrandId == context.BrandId && discount.Active && (discount.StartsAt == null || discount.StartsAt <= EfValues.UtcNow()) && (discount.EndsAt == null || discount.EndsAt > EfValues.UtcNow())
                                                                          select new Grant(revision.Id, discount.PercentOff)).SingleOrDefaultAsync(token), ct);
        if (grant is null || order.Status is "expired" or "cancelled" || order.Percent != grant.Percent || order.Units != order.Quantity || order.Version != PricingPolicy.Version || order.Step != PricingPolicy.TomanStep) Reject(session);
        Currency currency = order.Currency == "TOMAN" ? Currency.Toman : Currency.Usd;
        Money original = currency == Currency.Toman ? PricingPolicy.TomanTotal(order.UnitUsdt, order.UnitToman, order.Quantity, 0, order.Automatic, order.Rate) : PricingPolicy.DiscountTotal(order.UnitUsdt, order.Quantity, 0, Currency.Usd);
        Money discounted = currency == Currency.Toman ? PricingPolicy.TomanTotal(order.UnitUsdt, order.UnitToman, order.Quantity, grant.Percent, order.Automatic, order.Rate) : PricingPolicy.DiscountTotal(order.UnitUsdt, order.Quantity, grant.Percent, Currency.Usd);
        Money saved = original.Subtract(discounted);
        await session.RunEfStepAsync(true, async (data, token) =>
        {
            await data.RequireMembershipAsync(context, verifiedActorId, false, false, token);
            data.Data.Add(new Entities.DiscountUse { DiscountId = discountId, OrderId = orderId, DiscountRevisionId = grant.Id, UnitsCovered = order.Quantity, SavedAmount = saved.Amount, SavedCurrency = currency.ToCode(), Status = "held" });
            return true;
        }, ct);
        return new(discountId, orderId, grant.Id, order.Quantity, saved, "held");
    }

    public async Task<bool> TransitionClaimAsync(DbSession session, BrandContext context, Guid discountId, Guid orderId, bool redeem, CancellationToken ct = default)
    {
        try
        {
            await LockBrandAsync(session, context, ct);
            return await session.RunEfStepAsync(true, async (data, token) =>
            {
                await data.RequireBrandCommandAsync(context.BrandId, token);
                await data.LockDiscountAsync(discountId, context.BrandId, token);
                await data.LockOrderAsync(orderId, token);
                var order = await (from value in data.Data.Set<Entities.Order>()
                                   join member in data.Data.Set<Entities.User>() on value.UserId equals member.Id
                                   where value.Id == orderId && member.BrandId == context.BrandId
                                   select new ClaimState(value.PaymentStatus, value.ReleaseAuthorizedAt != null, value.OrderKind, value.FulfillmentStatus, value.ResolutionKind,
                                       value.OrderKind == "product_purchase" && !value.IsPreorder && (value.PaymentMethod == "wallet" || value.PaymentMethod == "no_payment_required") &&
                                       (from fulfillment in data.Data.Set<Entities.Fulfillment>() join item in data.Data.Set<Entities.InventoryItem>() on fulfillment.Id equals item.AssignedFulfillmentId where fulfillment.OrderId == value.Id && fulfillment.Kind == "local" && (fulfillment.Status == "acquired" || fulfillment.Status == "ready" || fulfillment.Status == "released") && item.State == "allocated" select fulfillment.Id).LongCount() == value.Quantity &&
                                       data.Data.Set<Entities.Fulfillment>().LongCount(fulfillment => fulfillment.OrderId == value.Id) == value.Quantity && !data.Data.Set<Entities.SupplierRequest>().Any(request => request.OrderId == value.Id))).SingleOrDefaultAsync(token);
                if (order is null) data.RejectScope("Coupon or pricing operation is unavailable.");
                var claim = await data.Data.Set<Entities.DiscountUse>().SingleOrDefaultAsync(value => value.DiscountId == discountId && value.OrderId == orderId, token);
                if (claim is null) data.RejectScope("Coupon or pricing operation is unavailable.");
                string target = redeem ? "redeemed" : "released";
                if (claim.Status == target) return false;
                if (claim.Status != "held" || (redeem ? order.Payment is not ("paid" or "not_required") || !(order.Release || order.ImmediateLocal || (order.Kind == "prepaid_units" && order.Fulfillment == "completed")) : order.Payment is not ("expired" or "cancelled") && order.Resolution is not ("local_preorder_cancellation" or "immediate_crypto_shortage_credit" or "supplier_wallet_credit"))) data.RejectScope("Coupon or pricing operation is unavailable.");
                claim.Status = target; claim.TransitionedAt = await data.BrandClockAsync(context.BrandId, token);
                if (redeem) { var discount = await data.Data.Set<Entities.Discount>().SingleAsync(value => value.Id == discountId, token); discount.RedeemedCount = checked(discount.RedeemedCount + claim.UnitsCovered); }
                return true;
            }, ct);
        }
        catch { Poison(session); throw; }
    }

    public async Task<Guid?> ScheduleFxAsync(BrandContext context, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginAsync(ct);
        await session.AdmitWriterAsync(context, ct);
        var state = await session.RunEfStepAsync(false, async (data, token) =>
        {
            await data.LockBrandAsync(context.BrandId, token, true);
            return await data.Data.Set<Entities.Brand>().Where(value => value.Id == context.BrandId && value.BotId == context.BotId && value.Active).Select(value => new { Enabled = value.FxEnabled, Fetched = value.FxFetchedAt, Now = EfValues.UtcNow(), Source = value.FxSource }).SingleAsync(token);
        }, ct);
        if (!state.Enabled || (state.Source == PricingPolicy.FxSource && state.Fetched > state.Now.AddHours(-1) && state.Fetched <= state.Now)) return null;
        byte[] digest = SHA256.HashData(Encoding.ASCII.GetBytes("brand-fx-v1:" + context.BrandId.ToString("D") + ":" + (state.Now.Ticks / TimeSpan.TicksPerHour).ToString(CultureInfo.InvariantCulture)));
        Guid id = await OutboxStore.EnqueueAsync(session, JobIntent.BrandFx(context.BrandId), new Guid(digest.AsSpan(0, 16)), state.Now, ct);
        await session.CommitAsync(ct);
        return id;
    }

    public async Task ApplyFxAsync(DbSession session, JobLease lease, long expectedPolicyRevision, ReadOnlyMemory<byte> response, CancellationToken ct = default)
    {
        try
        {
            decimal rate = PricingPolicy.ParseNobitex(response);
            Guid brand = FxBrand(lease);
            await OutboxStore.RenewAsync(session, lease, TimeSpan.FromSeconds(30), ct);
            await session.RunEfStepAsync(true, async (data, token) =>
            {
                await data.RequireFxLeaseAsync(lease, brand, token);
                await data.LockBrandUpdateAsync(brand, token);
                var row = await data.Data.Set<Entities.Brand>().SingleOrDefaultAsync(value => value.Id == brand && value.Active && value.FxEnabled && value.PolicyVersion == expectedPolicyRevision, token);
                if (row is null) data.RejectScope("Coupon or pricing operation is unavailable.");
                DateTime now = await data.BrandClockAsync(brand, token);
                row.CurrentFx = rate; row.FxSource = PricingPolicy.FxSource; row.FxFetchedAt = now; row.FxErrorCode = null; row.PolicyVersion++; row.UpdatedAt = now;
                return true;
            }, ct);
            await OutboxStore.CompleteAsync(session, lease, ct);
        }
        catch { Poison(session); throw; }
    }

    public async Task FailFxAsync(DbSession session, JobLease lease, long expectedPolicyRevision, CancellationToken ct = default)
    {
        try
        {
            Guid brand = FxBrand(lease);
            await OutboxStore.RenewAsync(session, lease, TimeSpan.FromSeconds(30), ct);
            await session.RunEfStepAsync(true, async (data, token) =>
            {
                await data.RequireFxLeaseAsync(lease, brand, token);
                await data.LockBrandUpdateAsync(brand, token);
                var row = await data.Data.Set<Entities.Brand>().SingleOrDefaultAsync(value => value.Id == brand && value.Active && value.FxEnabled && value.PolicyVersion == expectedPolicyRevision, token);
                if (row is null) data.RejectScope("Coupon or pricing operation is unavailable.");
                row.FxErrorCode = "fx_unavailable";
                return true;
            }, ct);
            await OutboxStore.RetryAsync(session, lease, TimeSpan.FromMinutes(1), 5, JobError.Transient, ct);
        }
        catch { Poison(session); throw; }
    }

    private static byte[] Digest(BrandContext context, string code)
    {
        byte[] bytes = Encoding.UTF8.GetBytes("coupon-lookup-v1:" + context.BrandId.ToString("D") + ":" + code);
        try { return SHA256.HashData(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static byte[] Commitment(CouponTerms terms, byte[] digest)
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(digest); writer.Write((int)terms.Kind); writer.Write(ExactDecimal.Format(terms.Percent));
            writer.Write((terms.ProductId ?? Guid.Empty).ToByteArray()); writer.Write((terms.MemberId ?? Guid.Empty).ToByteArray()); writer.Write((terms.SourceOrderId ?? Guid.Empty).ToByteArray()); writer.Write((terms.BatchId ?? Guid.Empty).ToByteArray());
            writer.Write(terms.MaximumUses ?? -1); writer.Write(terms.MaximumPerMember ?? -1); writer.Write(terms.StartsAt?.Ticks ?? -1); writer.Write(terms.EndsAt?.Ticks ?? -1);
        }
        return SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)));
    }
    private static string Normalize(string code)
    {
        if (code is null) throw new PricingRejectedException();
        string normalized = code.Trim().ToUpperInvariant();
        if (normalized.Length is < 1 or > 64 || normalized.Any(char.IsControl)) throw new PricingRejectedException();
        return normalized;
    }
    private static async Task LockBrandAsync(DbSession session, BrandContext context, CancellationToken ct)
    {
        await session.AdmitWriterAsync(context, ct);
        int count = await session.RunEfStepAsync(false, (data, token) => data.LockBrandUpdateAsync(context.BrandId, token), ct);
        if (count != 1) Reject(session);
    }
    private static Task<BrandUser> RequireCustomerAsync(DbSession session, BrandContext context, long actor, CancellationToken ct) => session.RunEfStepAsync(false, async (data, token) =>
    {
        var member = (await data.RequireMembershipAsync(context, actor, false, false, token))!;
        return BrandUserStore.Project(member);
    }, ct);
    private static async Task<CouponClaim?> ReadClaimAsync(DbSession session, Guid discount, Guid order, CancellationToken ct)
    {
        var claim = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.DiscountUse>().Where(value => value.DiscountId == discount && value.OrderId == order).Select(value => new { value.DiscountRevisionId, value.UnitsCovered, value.SavedAmount, value.SavedCurrency, value.Status }).SingleOrDefaultAsync(token), ct);
        return claim is null ? null : new(discount, order, claim.DiscountRevisionId, claim.UnitsCovered, new Money(claim.SavedAmount, CurrencyCodes.Parse(claim.SavedCurrency)), claim.Status);
    }
    private static IQueryable<Entities.Discount> CustomerCoupons(EfSession data, BrandContext context, Guid member) => data.Data.Set<Entities.Discount>().Where(value => value.BrandId == context.BrandId && (value.EligibleUserId == null || value.EligibleUserId == member) && (value.Kind != "prepaid_entitlement" || data.Data.Set<Entities.Order>().Any(order => order.Id == value.SourceOrderId && order.UserId == member && order.ProductId == value.ProductId && order.OrderKind == "prepaid_units" && order.PaymentStatus == "paid")));
    private static Guid FxBrand(JobLease lease) => lease.Intent.Kind == JobKind.RefreshFx && lease.Intent.BrandId is Guid brand ? brand : throw new PricingRejectedException();
    private static DbValue Uuid(Guid? value) => value is Guid id ? DbValue.Uuid(id) : DbValue.Null(NpgsqlDbType.Uuid);
    private static DbValue Number(long? value) => value is long number ? DbValue.Bigint(number) : DbValue.Null(NpgsqlDbType.Bigint);
    private static DbValue Time(DateTime? value) => value is DateTime time ? DbValue.Utc(time) : DbValue.Null(NpgsqlDbType.TimestampTz);
    private static void Poison(DbSession session) { if (session.State == DbSessionState.Active) session.RejectApplication(); }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Reject(DbSession session) { Poison(session); throw new UnauthorizedAccessException("Coupon or pricing operation is unavailable."); }
    private sealed record StoredCode(byte[] Content)
    {
        public override string ToString() => "Coupon code [redacted]";
    }
    private sealed record Grant(Guid Id, decimal Percent);
    private sealed record ClaimOrder(int Quantity, string Currency, decimal UnitUsdt, decimal? UnitToman, bool Automatic, FxRate? Rate, decimal? Percent, int? Units, string Version, string Status, decimal Step);
    private sealed record ClaimState(string Payment, bool Release, string Kind, string Fulfillment, string? Resolution, bool ImmediateLocal);
}
