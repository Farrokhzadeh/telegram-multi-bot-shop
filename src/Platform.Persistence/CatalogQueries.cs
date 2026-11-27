using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Platform.Core;
using Platform.Persistence.Configurations;
using Platform.Persistence.Entities;
using Platform.Security;

namespace Platform.Persistence;

internal sealed record CatalogSource(Guid Id, string Provider, string ProductRef, string? VariantRef, bool Enabled, long Revision, long ObservationRevision, long? ObservedSourceRevision, string? Account, DateTime? ObservedAt, string Availability, int? Quantity, decimal? Cost, string? Currency, string Eligibility, string? Quote);
internal sealed record CatalogState(Guid ProductId, Guid Member, long PolicyRevision, long ProductRevision, long ListingRevision, ProductPresentation Presentation, string Kind, string Policy, bool LocalFirst, long Local, decimal UnitUsdt, decimal? UnitToman, bool AutoToman, bool FxEnabled, FxRate? Fx, TimeSpan MaximumFxAge, string Market, bool Wallet, bool Crypto, bool Bank, int Minimum, int Maximum, CatalogSource? Source, DateTime Now);
internal static class CatalogQueries
{
    internal static async Task<CatalogSource?> SourceAsync(EfSession data, Guid? id, Guid product, CancellationToken ct, bool locked = false)
    {
        if (id is null) return null;
        if (locked) await data.LockSourceAsync(id.Value, ct);
        return await data.Data.Set<ProductSupplierSource>().Where(value => value.Id == id && value.ProductId == product).Select(value => new CatalogSource(value.Id, value.ProviderCode, value.ExternalProductId, value.ExternalVariantId, value.Enabled, value.Revision, value.ObservationRevision, value.ObservedSourceRevision, value.ObservedAccountRef, value.ObservedAt, value.AvailabilityCode, value.AvailableQuantity, value.ObservedUnitCost, value.ObservedCurrency, value.EligibilityCode, value.QuoteRef)).SingleOrDefaultAsync(ct);
    }
    internal static async Task<CatalogState?> LoadAsync(EfSession data, BrandContext context, long actor, Guid product, CancellationToken ct, bool locked = false)
    {
        if (locked) { await data.LockBrandUpdateAsync(context.BrandId, ct); await data.LockListingAsync(context.BrandId, product, ct); }
        var state = await (from member in data.Data.Set<User>()
                           join brand in data.Data.Set<Entities.Brand>() on member.BrandId equals brand.Id
                           join listing in data.Data.Set<BrandProduct>() on brand.Id equals listing.BrandId
                           join master in data.Data.Set<Product>() on listing.ProductId equals master.Id
                           where member.BrandId == context.BrandId && member.TelegramId == actor && member.BlockedAt == null && member.BannedAt == null && brand.BotId == context.BotId && brand.Active &&
                               master.Id == product && master.Active && listing.Visible && (locked || !brand.RequireChannelJoin)
                           select new
                           {
                               Member = member.Id,
                               brand.PublicName,
                               brand.Market,
                               brand.PolicyVersion,
                               Wallet = brand.AllowWallet && listing.AllowWallet,
                               Crypto = brand.AllowCrypto && listing.AllowCrypto,
                               Bank = brand.AllowBank && listing.AllowBank,
                               brand.FxEnabled,
                               brand.CurrentFx,
                               brand.FxSource,
                               brand.FxFetchedAt,
                               brand.MaxExchangeRateAgeSeconds,
                               ProductRevision = master.Revision,
                               ListingRevision = listing.Revision,
                               master.FulfillmentKind,
                               master.AvailabilityPolicy,
                               master.LocalFirst,
                               master.ActiveSupplierSourceId,
                               listing.PriceUsdt,
                               listing.PriceToman,
                               listing.AutoPriceToman,
                               listing.MinQuantity,
                               listing.MaxQuantity,
                               Title = listing.TitleMode == "override" ? listing.TitleOverride! : master.Title,
                               Description = listing.DescriptionMode == "override" ? listing.DescriptionOverride : listing.DescriptionMode == "clear" ? null : master.Description,
                               Icon = listing.IconMode == "override" ? listing.IconOverride : listing.IconMode == "clear" ? null : master.IconRef,
                               Guidance = listing.GuidanceMode == "override" ? listing.GuidanceOverride : listing.GuidanceMode == "clear" ? null : master.Guidance
                           }).SingleOrDefaultAsync(ct);
        if (state is null) return null;
        long local = await data.Data.Set<Order>().AnyAsync(value => value.ProductId == product && value.IsPreorder && (value.PaymentStatus == "paid" || value.PaymentStatus == "not_required") && (value.FulfillmentStatus == "waiting_stock" || value.FulfillmentStatus == "requires_admin"), ct) ? 0 :
            await data.Data.Set<InventoryItem>().LongCountAsync(value => value.ProductId == product && value.State == "available", ct);
        var source = locked ? await SourceAsync(data, state.ActiveSupplierSourceId, product, ct, true) : null;
        DateTime now = await data.BrandClockAsync(context.BrandId, ct);
        FxRate? rate = state.CurrentFx is decimal current && state.FxSource is string origin && state.FxFetchedAt is DateTime fetched ? new(current, origin, fetched) : null;
        return new(product, state.Member, state.PolicyVersion, state.ProductRevision, state.ListingRevision, new(state.PublicName, state.Title, state.Description, state.Icon, state.Guidance), state.FulfillmentKind, state.AvailabilityPolicy, state.LocalFirst, local, state.PriceUsdt, state.PriceToman, state.AutoPriceToman, state.FxEnabled, rate, TimeSpan.FromSeconds(state.MaxExchangeRateAgeSeconds), state.Market, state.Wallet, state.Crypto, state.Bank, state.MinQuantity, state.MaxQuantity, source, now);
    }
    internal static IQueryable<Discount> Eligible(EfSession data, BrandContext context, Guid product, Guid member) => data.Data.Set<Discount>().Where(value => value.BrandId == context.BrandId && value.Active && (value.ProductId == null || value.ProductId == product) && (value.EligibleUserId == null || value.EligibleUserId == member) && (value.StartsAt == null || value.StartsAt <= EfValues.UtcNow()) && (value.EndsAt == null || value.EndsAt > EfValues.UtcNow()) && (value.Kind != "prepaid_entitlement" || data.Data.Set<Order>().Any(order => order.Id == value.SourceOrderId && order.UserId == member && order.ProductId == product && order.OrderKind == "prepaid_units" && order.PaymentStatus == "paid")));
    internal static async Task<IReadOnlyList<QuoteCoupon>> CouponsAsync(EfSession data, BrandContext context, Guid product, Guid member, CancellationToken ct, bool locked = false)
    {
        var eligible = Eligible(data, context, product, member);
        var candidates = from discount in eligible join revision in data.Data.Set<DiscountRevision>() on new { Id = discount.Id, Revision = discount.Revision } equals new { Id = revision.DiscountId, Revision = revision.RevisionNo } select new { Discount = discount, Revision = revision.Id, Bytes = discount.CodeContent };
        if (locked) await data.LockDiscountsAsync(eligible.Where(value => data.Data.Set<DiscountRevision>().Any(revision => revision.DiscountId == value.Id && revision.RevisionNo == value.Revision)).OrderBy(value => value.Id).Select(value => value.Id), ct);
        var rows = await candidates.OrderBy(value => value.Discount.Id).Select(value => new
        {
            value.Discount.Id,
            value.Revision,
            value.Bytes,
            value.Discount.PercentOff,
            value.Discount.MaxTotalUses,
            value.Discount.RedeemedCount,
            value.Discount.UsageAdjustmentUnits,
            value.Discount.MaxUsesPerUser,
            Held = data.Data.Set<DiscountUse>().Where(use => use.DiscountId == value.Discount.Id && use.Status == "held").Sum(use => (decimal?)use.UnitsCovered).GetValueOrDefault().ToString(),
            MemberUsed = (from use in data.Data.Set<DiscountUse>() join order in data.Data.Set<Order>() on use.OrderId equals order.Id where use.DiscountId == value.Discount.Id && order.UserId == member && (use.Status == "held" || use.Status == "redeemed") select (decimal?)use.UnitsCovered).Sum().GetValueOrDefault().ToString(),
            Reusable = (from use in data.Data.Set<DiscountUse>() join order in data.Data.Set<Order>() on use.OrderId equals order.Id where use.DiscountId == value.Discount.Id && order.UserId == member && use.Status == "held" && order.PaymentStatus == "awaiting_payment" && !data.Data.Set<Payment>().Any(payment => payment.OrderId == order.Id && (payment.FirstPaymentObservedAt != null || payment.ReceiptAssetRef != null || payment.Status == "observed" || payment.Status == "confirming" || payment.Status == "settled" || payment.Status == "review")) select (decimal?)use.UnitsCovered).Sum().GetValueOrDefault().ToString()
        }).Take(1001).ToArrayAsync(ct);
        if (rows.Length > 1000) throw new PricingRejectedException();
        var result = new List<QuoteCoupon>(rows.Length);
        foreach (var row in rows)
        {
            BigInteger held = BigInteger.Parse(row.Held, NumberStyles.Number, CultureInfo.InvariantCulture), memberUsed = BigInteger.Parse(row.MemberUsed, NumberStyles.Number, CultureInfo.InvariantCulture), reusable = BigInteger.Parse(row.Reusable, NumberStyles.Number, CultureInfo.InvariantCulture);
            BigInteger? capacity = PricingPolicy.Remaining(row.MaxTotalUses, row.RedeemedCount, row.UsageAdjustmentUnits, held - reusable);
            if (row.MaxUsesPerUser is long limit) { BigInteger own = BigInteger.Max(0, (BigInteger)limit - memberUsed + reusable); capacity = capacity is BigInteger global ? BigInteger.Min(global, own) : own; }
            try { result.Add(new(row.Id, row.Revision, Encoding.UTF8.GetString(row.Bytes), row.PercentOff, capacity)); }
            finally { CryptographicOperations.ZeroMemory(row.Bytes); }
        }
        return result;
    }
}
