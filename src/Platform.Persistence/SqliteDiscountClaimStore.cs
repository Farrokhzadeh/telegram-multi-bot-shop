using System.Numerics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Platform.Core;
using Platform.Persistence.Configurations;
using Platform.Security;

namespace Platform.Persistence;

public static class SqliteDiscountClaimStore
{
    internal static async Task HoldAsync(SqliteTransaction tx, BrandContext context, Guid member, Guid order, Guid product, CheckoutQuote quote, CancellationToken ct)
    {
        QuoteTerms terms = quote.Terms;
        if (terms.DiscountId is not Guid coupon || terms.DiscountRevisionId is not Guid revision || terms.DiscountPercent is not decimal percent || terms.DiscountUnits != terms.Quantity || percent is <= 0 or >= 100 || terms.Quantity <= 0) throw new PricingRejectedException();
        await SqliteSessionFactory.StepEfAsync(tx, true, async (data, token) =>
        {
            await data.RequireOrderCommandAsync(context, order, token);
            var grant = await (from value in data.Data.Set<Entities.Discount>()
                               join snapshot in data.Data.Set<Entities.DiscountRevision>() on new { Id = value.Id, Revision = value.Revision } equals new { Id = snapshot.DiscountId, Revision = snapshot.RevisionNo }
                               join source in data.Data.Set<Entities.Order>() on order equals source.Id
                               join user in data.Data.Set<Entities.User>() on source.UserId equals user.Id
                               where value.Id == coupon && value.BrandId == context.BrandId && user.BrandId == context.BrandId && user.Id == member
                               select new { Discount = value, Revision = snapshot.Id, Order = source }).SingleOrDefaultAsync(token);
            if (grant is null || grant.Revision != revision || grant.Discount.PercentOff != percent || !grant.Discount.Active || grant.Order.Quantity != terms.Quantity || grant.Order.DiscountPctSnapshot != percent || grant.Order.DiscountUnitsSnapshot != terms.Quantity || grant.Order.PriceCalculationVersion != terms.Version || grant.Order.PricingRoundingStepSnapshot != terms.RoundingStep || grant.Order.ProductId != product || grant.Order.UserId != member || grant.Discount.ProductId != null && grant.Discount.ProductId != product || grant.Discount.EligibleUserId != null && grant.Discount.EligibleUserId != member) throw new PricingRejectedException();
            DateTime now = await data.BrandClockAsync(context.BrandId, token);
            if (grant.Discount.StartsAt > now || grant.Discount.EndsAt <= now) throw new PricingRejectedException();
            if (grant.Discount.Kind == "prepaid_entitlement" && !await data.Data.Set<Entities.Order>().AnyAsync(value => value.Id == grant.Discount.SourceOrderId && value.UserId == member && value.ProductId == product && value.OrderKind == "prepaid_units" && value.PaymentStatus == "paid", token)) throw new PricingRejectedException();
            decimal held = await data.Data.Set<Entities.DiscountUse>().Where(value => value.DiscountId == coupon && value.Status == "held").SumAsync(value => (decimal?)value.UnitsCovered, token) ?? 0m;
            decimal used = await (from value in data.Data.Set<Entities.DiscountUse>() join source in data.Data.Set<Entities.Order>() on value.OrderId equals source.Id where value.DiscountId == coupon && source.UserId == member && (value.Status == "held" || value.Status == "redeemed") select (decimal?)value.UnitsCovered).SumAsync(token) ?? 0m;
            if (grant.Discount.MaxTotalUses is long maximum && (BigInteger)maximum + grant.Discount.UsageAdjustmentUnits - grant.Discount.RedeemedCount - (BigInteger)held < terms.Quantity || grant.Discount.MaxUsesPerUser is long per && (BigInteger)per - (BigInteger)used < terms.Quantity) throw new PricingRejectedException();
            Money before = terms.Currency == Currency.Toman ? PricingPolicy.TomanTotal(terms.UnitUsdt, terms.UnitToman, terms.Quantity, 0, terms.AutoToman, terms.Rate) : PricingPolicy.DiscountTotal(terms.UnitUsdt, terms.Quantity, 0, Currency.Usd);
            Money after = terms.Currency == Currency.Toman ? PricingPolicy.TomanTotal(terms.UnitUsdt, terms.UnitToman, terms.Quantity, percent, terms.AutoToman, terms.Rate) : PricingPolicy.DiscountTotal(terms.UnitUsdt, terms.Quantity, percent, Currency.Usd);
            Money saved = before.Subtract(after);
            data.Data.Add(new Entities.DiscountUse { DiscountId = coupon, OrderId = order, DiscountRevisionId = revision, UnitsCovered = terms.Quantity, SavedAmount = saved.Amount, SavedCurrency = saved.Currency.ToCode(), Status = "held" }); return true;
        }, ct);
    }
    internal static Task<bool> TransitionAsync(SqliteTransaction tx, BrandContext context, Guid order, bool redeem, CancellationToken ct) => SqliteSessionFactory.StepEfAsync(tx, true, async (data, token) =>
    {
        var state = await (from value in data.Data.Set<Entities.DiscountUse>()
                           join source in data.Data.Set<Entities.Order>() on value.OrderId equals source.Id
                           join user in data.Data.Set<Entities.User>() on source.UserId equals user.Id
                           join brand in data.Data.Set<Entities.Brand>() on user.BrandId equals brand.Id
                           where value.OrderId == order && brand.Id == context.BrandId && brand.BotId == context.BotId && brand.Active
                           select new
                           {
                               Claim = value,
                               Order = source,
                               Allocated = (from fulfillment in data.Data.Set<Entities.Fulfillment>() join item in data.Data.Set<Entities.InventoryItem>() on fulfillment.Id equals item.AssignedFulfillmentId where fulfillment.OrderId == source.Id && fulfillment.Kind == "local" && (fulfillment.Status == "acquired" || fulfillment.Status == "ready" || fulfillment.Status == "released") && item.State == "allocated" select fulfillment.Id).LongCount(),
                               Total = data.Data.Set<Entities.Fulfillment>().LongCount(fulfillment => fulfillment.OrderId == source.Id),
                               Remote = data.Data.Set<Entities.SupplierRequest>().Any(request => request.OrderId == source.Id)
                           }).SingleOrDefaultAsync(token);
        if (state is null) return false;
        string target = redeem ? "redeemed" : "released";
        if (state.Claim.Status == target) return false;
        bool immediate = state.Order.OrderKind == "product_purchase" && !state.Order.IsPreorder && state.Order.PaymentMethod is ("wallet" or "no_payment_required") && state.Allocated == state.Order.Quantity && state.Total == state.Order.Quantity && !state.Remote;
        bool permitted = redeem ? state.Order.PaymentStatus is ("paid" or "not_required") && (state.Order.ReleaseAuthorizedAt != null || immediate || state.Order.OrderKind == "prepaid_units" && state.Order.FulfillmentStatus == "completed") : state.Order.PaymentStatus is ("expired" or "cancelled") || state.Order.ResolutionKind is ("local_preorder_cancellation" or "immediate_crypto_shortage_credit" or "supplier_wallet_credit");
        if (state.Claim.Status != "held" || !permitted) data.RejectScope("Discount claim transition is unavailable.");
        await data.RequireOrderCommandAsync(context, order, token);
        state.Claim.Status = target; state.Claim.TransitionedAt = await data.BrandClockAsync(context.BrandId, token);
        if (redeem)
        {
            var discount = await data.Data.Set<Entities.Discount>().SingleOrDefaultAsync(value => value.Id == state.Claim.DiscountId && value.BrandId == context.BrandId, token);
            if (discount is null) data.RejectConflict("Discount redemption was superseded.");
            discount.RedeemedCount = checked(discount.RedeemedCount + state.Claim.UnitsCovered);
        }
        return true;
    }, ct);
}
