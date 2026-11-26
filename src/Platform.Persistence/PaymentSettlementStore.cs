using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Security.Cryptography;
using Platform.Core;

namespace Platform.Persistence;

public enum PaymentSettlementOutcome { Pending, Review, Settled, Replayed }
public sealed record PaymentSettlement(PaymentSettlementOutcome Outcome, ExternalSettlementRoute Route, Guid? Credit = null);

public sealed class PaymentSettlementStore(DbSessionFactory sessions, CatalogStore catalog)
{
    public Task<PaymentSettlement> SettleAsync(BrandContext context, Guid payment,
        Func<InvoiceExpectation, CancellationToken, Task<ProviderPaymentSnapshot>> lookup, CancellationToken ct = default) => SettleCoreAsync(context, payment, lookup, null, ct);
    public Task<PaymentSettlement> SettleForBrandAsync(BrandContext context, long expectedRevision, Guid payment,
        Func<InvoiceExpectation, CancellationToken, Task<ProviderPaymentSnapshot>> lookup, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        return SettleCoreAsync(context, payment, lookup, expectedRevision, ct);
    }
    private async Task<PaymentSettlement> SettleCoreAsync(BrandContext context, Guid payment,
        Func<InvoiceExpectation, CancellationToken, Task<ProviderPaymentSnapshot>> lookup, long? writerRevision, CancellationToken ct)
    {
        State before;
        await using (var read = await sessions.BeginAsync(ct))
        {
            if (writerRevision is long revision) await BrandWriterStore.RequireAsync(read, context, revision, ct);
            else await read.AdmitWriterAsync(context, ct);
            before = await LoadAsync(read, context, payment, ct);
        }
        if (before.Status == "settled") return new(PaymentSettlementOutcome.Replayed, ExternalSettlementRoute.AlreadyAccepted, before.Credit);
        if (!Ready(before)) return new(before.Status == "review" ? PaymentSettlementOutcome.Review : PaymentSettlementOutcome.Pending, ExternalSettlementRoute.Review);
        ProviderPaymentSnapshot remote = await lookup(Expectation(before), ct);
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            await LockAsync(session, context, before, ct, writerRevision);
            State data = await LoadAsync(session, context, payment, ct);
            if (data.Status == "settled") return new(PaymentSettlementOutcome.Replayed, ExternalSettlementRoute.AlreadyAccepted, data.Credit);
            if (!Ready(data)) return new(data.Status == "review" ? PaymentSettlementOutcome.Review : PaymentSettlementOutcome.Pending, ExternalSettlementRoute.Review);
            if (Stopwatch.GetElapsedTime(remote.ObservedAt) > TimeSpan.FromSeconds(30) || Expectation(data) != Expectation(before) ||
                data.Hash is null || before.Hash is null || !CryptographicOperations.FixedTimeEquals(data.Hash, before.Hash)) throw new PaymentNoticeRejectedException();
            bool match = remote.Invoice == Expectation(data) && remote.Received == data.Expected;
            if (!match || remote.Status != "finished")
            {
                if (remote.Invoice == Expectation(data) && remote.Received <= data.Expected && remote.Status is "waiting" or "confirming" or "confirmed" or "sending")
                    return new(PaymentSettlementOutcome.Pending, ExternalSettlementRoute.Review);
                await ChangePaymentAsync(session, context, payment, row => row.Status = "review", ct);
                await session.CommitAsync(ct);
                return new(PaymentSettlementOutcome.Review, ExternalSettlementRoute.Review);
            }
            await ChangePaymentAsync(session, context, payment, row => row.Status = "settled", ct);
            ExternalSettlementRoute route;
            Guid? credit = null;
            if (data.Order is not Guid order)
            {
                route = ExternalSettlementRoute.Topup;
                credit = await CreditAsync(session, data, "wallet_topup", ct);
            }
            else
            {
                if (data.OrderStatus is "awaiting_payment" or "under_review" or "confirmation_pending" && data.First > data.Deadline)
                {
                    await ChangeOrderAsync(session, order, (row, now) => { row.PaymentStatus = "expired"; row.FulfillmentStatus = "cancelled"; row.ResolutionKind = "unpaid_expiry"; row.ResolutionAt = now; }, ct);
                    data = data with { OrderStatus = "expired" };
                }
                if (data.OrderStatus is "expired" or "cancelled")
                {
                    route = ExternalSettlementRoute.LateCredit;
                    credit = await CreditAsync(session, data, "late_crypto_credit", ct);
                    await ReleaseAsync(session, context, order, ct);
                }
                else if (data.OrderStatus is "awaiting_payment" or "under_review" or "confirmation_pending")
                {
                    await ChangeOrderAsync(session, order, (row, _) => { row.PaymentStatus = "paid"; row.PaymentConfirmedAt = data.Confirmed!.Value; }, ct);
                    route = await DispatchAsync(session, context, data, ct);
                    if (route == ExternalSettlementRoute.LocalShortageCredit)
                    {
                        credit = await CreditAsync(session, data, "immediate_crypto_shortage_credit", ct);
                        await ChangeOrderAsync(session, order, (row, now) => { row.FulfillmentStatus = "compensated"; row.ResolutionKind = "immediate_crypto_shortage_credit"; row.ResolutionAt = now; row.ResolutionWalletEntryId = credit.Value; }, ct);
                        await ReleaseAsync(session, context, order, ct);
                    }
                }
                else throw new PaymentNoticeRejectedException();
                await session.RunEfStepAsync(true, async (ef, token) =>
                {
                    await ef.RequireOrderCommandAsync(context, order, token); await ef.LockExpiryJobsAsync(order, token);
                    var jobs = await ef.Data.Set<Entities.Job>().Where(value => value.OrderId == order && value.JobKind == "expire_order_payment" && (value.Status == "pending" || value.Status == "retry")).Take(1001).ToArrayAsync(token);
                    if (jobs.Length > 1000) ef.RejectScope("Payment expiry job bound exceeded.");
                    DateTime now = await ef.BrandClockAsync(context.BrandId, token);
                    foreach (var job in jobs) { job.Status = "cancelled"; job.CompletedAt = now; }
                    return true;
                }, ct);
                await NoticeAsync(session, context, data, ct);
            }
            if (credit is Guid entry) await ChangePaymentAsync(session, context, payment, row => row.CompensatingWalletEntryId = entry, ct);
            await session.CommitAsync(ct);
            return new(PaymentSettlementOutcome.Settled, route, credit);
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }

    internal async Task<ExternalSettlementRoute> DispatchAsync(DbSession session, BrandContext context, State data, CancellationToken ct)
    {
        Guid order = data.Order!.Value, product = data.Product!.Value;
        if (data.Kind == "prepaid_units")
        {
            await new PricingStore(sessions).IssuePaidSourceAsync(session, context, order, ct);
            return ExternalSettlementRoute.Prepaid;
        }
        if (data.Preorder)
        {
            await FulfillmentAsync(session, order, "waiting_stock", null, ct);
            await OutboxStore.EnqueueAsync(session, JobIntent.Product(JobKind.DrainLocalPreorders, product), order, DateTime.UtcNow, ct);
            return ExternalSettlementRoute.LocalPreorder;
        }
        if (data.Fulfillment == "manual")
        {
            await FulfillmentAsync(session, order, "requires_admin", null, ct);
            return ExternalSettlementRoute.LocalGoods;
        }
        var current = await session.RunEfStepAsync(false, (ef, token) => ef.Data.Set<Entities.Product>().Where(value => value.Id == product).Select(value => value.FulfillmentKind).SingleAsync(token), ct);
        if (current != data.Fulfillment)
        {
            await FulfillmentAsync(session, order, "requires_admin", "source_ineligible", ct);
            if (data.Fulfillment == "supplier") await catalog.BindAcceptedAsync(session, context, product, order, data.Quantity, ct);
            return ExternalSettlementRoute.Review;
        }
        bool queued = await session.RunEfStepAsync(false, (ef, token) => ef.Data.Set<Entities.Order>().AnyAsync(value => value.ProductId == product && value.Id != order && value.IsPreorder && (value.PaymentStatus == "paid" || value.PaymentStatus == "not_required") && (value.FulfillmentStatus == "waiting_stock" || value.FulfillmentStatus == "requires_admin"), token), ct);
        int wanted = queued || (data.Fulfillment == "supplier" && !data.LocalFirst) ? 0 : data.Quantity;
        var stock = await CheckoutStore.ReadStockAsync(session, product, wanted, ct);
        if (data.Fulfillment == "local" && stock.Count != data.Quantity)
        {
            if (data.Method == "crypto") return ExternalSettlementRoute.LocalShortageCredit;
            await FulfillmentAsync(session, order, "waiting_stock", null, ct);
            await OutboxStore.EnqueueAsync(session, JobIntent.Product(JobKind.DrainLocalPreorders, product), order, DateTime.UtcNow, ct);
            return ExternalSettlementRoute.LocalGoods;
        }
        string? review = null;
        if (data.Fulfillment == "supplier" && stock.Count < data.Quantity)
            review = await catalog.BindAcceptedAsync(session, context, product, order, data.Quantity - stock.Count, ct);
        await new CheckoutStore(sessions).AcquireAsync(session, context, order, stock, ct);
        await FulfillmentAsync(session, order, review is null ? "acquiring" : "requires_admin", review, ct);
        if (stock.Count == data.Quantity) await OutboxStore.EnqueueAsync(session, JobIntent.Order(JobKind.PrepareManifest, context.BrandId, order), order, DateTime.UtcNow, ct);
        return data.Fulfillment == "supplier" ? ExternalSettlementRoute.Supplier : ExternalSettlementRoute.LocalGoods;
    }

    private static async Task<Guid> CreditAsync(DbSession session, State data, string movement, CancellationToken ct)
    {
        Money amount = ExternalPaymentPolicy.FrozenCredit(data.Due);
        return await WalletOperations.PostAsync(session, data.Member, amount, null,
            new Entities.WalletEntry { Id = Guid.CreateVersion7(), MovementKind = movement, IdempotencyKey = "payment-credit:" + data.Id.ToString("D"), OrderId = data.Order, PaymentId = data.Id, Reason = "verified_original_invoice" }, ct);
    }
    private async Task ReleaseAsync(DbSession session, BrandContext context, Guid order, CancellationToken ct)
    {
        Guid? coupon = await session.RunEfStepAsync(false, (ef, token) => ef.Data.Set<Entities.DiscountUse>().Where(value => value.OrderId == order && value.Status == "held").Select(value => (Guid?)value.DiscountId).SingleOrDefaultAsync(token), ct);
        if (coupon is Guid id) await new PricingStore(sessions).TransitionClaimAsync(session, context, id, order, false, ct);
    }
    internal static async Task NoticeAsync(DbSession session, BrandContext context, State data, CancellationToken ct)
    {
        Guid delivery = await session.RunEfStepAsync(true, async (ef, token) =>
        {
            await ef.RequirePaymentCommandAsync(context, data.Id, token);
            long? message = await ef.Data.Set<Entities.TelegramDelivery>().Where(value => value.OrderId == data.Order && value.Purpose == "order_status" && value.Status == "succeeded").OrderBy(value => value.CreatedAt).ThenBy(value => value.Id).Select(value => value.TelegramMessageId).FirstOrDefaultAsync(token);
            Guid id = Guid.CreateVersion7();
            ef.Data.Add(new Entities.TelegramDelivery { Id = id, BrandId = context.BrandId, RecipientKind = "customer_dm", UserId = data.Member, Purpose = "order_status", Action = message is null ? "send" : "edit", TargetMessageId = message, DedupeKey = "payment-status:" + data.Id.ToString("D"), OrderId = data.Order });
            return id;
        }, ct);
        await OutboxStore.EnqueueAsync(session, JobIntent.Telegram(context.BrandId, delivery), data.Id, DateTime.UtcNow, ct);
    }
    internal static async Task FulfillmentAsync(DbSession session, Guid order, string status, string? review, CancellationToken ct) => await ChangeOrderAsync(session, order, (row, _) => { row.FulfillmentStatus = status; row.ReviewCode = review; }, ct);
    private static Task<bool> ChangePaymentAsync(DbSession session, BrandContext context, Guid payment, Action<Entities.Payment> change, CancellationToken ct) => session.RunEfStepAsync(true, async (ef, token) =>
    {
        await ef.RequirePaymentCommandAsync(context, payment, token);
        var row = await ef.Data.Set<Entities.Payment>().SingleAsync(value => value.Id == payment, token); change(row); return true;
    }, ct);
    internal static Task<bool> ChangeOrderAsync(DbSession session, Guid order, Action<Entities.Order, DateTime> change, CancellationToken ct) => session.RunEfStepAsync(true, async (ef, token) =>
    {
        Guid member = await ef.Data.Set<Entities.Order>().Where(value => value.Id == order).Select(value => value.UserId).SingleAsync(token);
        await ef.RequireWalletCommandAsync(member, token); await ef.LockOrderAsync(order, token);
        var row = await ef.Data.Set<Entities.Order>().SingleAsync(value => value.Id == order, token);
        DateTime now = await ef.ClockAsync(ef.Data.Set<Entities.Order>().Where(value => value.Id == order), token);
        change(row, now); row.Revision = checked(row.Revision + 1); row.UpdatedAt = now; return true;
    }, ct);
    internal static async Task LockAsync(DbSession session, BrandContext context, State data, CancellationToken ct, long? writerRevision = null)
    {
        await session.RunEfStepAsync(false, async (ef, token) =>
        {
            await ef.LockUserAsync(data.Member, context.BrandId, token);
            if (data.Product is Guid product) await ef.LockProductAsync(product, token);
            return true;
        }, ct);
        if (writerRevision is long revision) await BrandWriterStore.RequireAsync(session, context, revision, ct);
        else { await session.AdmitWriterAsync(context, ct); await session.RunEfStepAsync(false, (ef, token) => ef.LockBrandUpdateAsync(context.BrandId, token), ct); }
        await session.RunEfStepAsync(false, async (ef, token) =>
        {
            if (data.Order is Guid order) await ef.LockOrderAsync(order, token);
            return await ef.LockPaymentAsync(data.Id, token);
        }, ct);
    }
    internal static async Task<State> LoadAsync(DbSession session, BrandContext context, Guid payment, CancellationToken ct)
    {
        State? data = await session.RunEfStepAsync(false, (ef, token) =>
            (from value in ef.Data.Set<Entities.Payment>()
             join order in ef.Data.Set<Entities.Order>() on value.OrderId equals order.Id into orders
             from order in orders.DefaultIfEmpty()
             join user in ef.Data.Set<Entities.User>() on (value.TopupUserId ?? (Guid?)order.UserId) equals user.Id
             join brand in ef.Data.Set<Entities.Brand>() on user.BrandId equals brand.Id
             where value.Id == payment && brand.Id == context.BrandId && brand.BotId == context.BotId
             select new State(value.Id, user.Id, value.OrderId, order == null ? null : order.ProductId, value.PaymentMethod, value.Status, value.AmountDue, value.ProviderPaymentRef, value.IdempotencyRef, value.NetworkAsset, value.ExpectedNetworkAmount, value.NetworkAmount, value.AmountReceived, value.ProviderStatusCode, value.IpnPayloadSha256, value.ConfirmedAt, value.FirstPaymentObservedAt, order == null ? null : order.PaymentDueAt, order == null ? null : order.PaymentStatus, order == null ? null : order.OrderKind, order == null ? 0 : order.Quantity, order == null ? null : order.FulfillmentKindSnapshot, order != null && order.LocalFirstSnapshot, order != null && order.IsPreorder, value.CompensatingWalletEntryId, value.ProviderCode, value.EvidenceAssetRef != null)).SingleOrDefaultAsync(token), ct);
        if (data is null) throw new UnauthorizedAccessException("Payment is unavailable.");
        return data;
    }
    private static bool Ready(State data) => data.Method == "crypto" && data.ProviderCode == "nowpayments" && data.Status == "confirming" && data.Provider is not null && data.Expected > 0 &&
        data.Tokens == data.Expected && data.Received == data.Due && data.ProviderStatus == "finished" && data.Hash is not null && data.Evidence && data.Confirmed is not null && data.First is not null;
    private static InvoiceExpectation Expectation(State data) => new(data.Provider!, data.Origin, data.Due, data.Asset == "USDTBSC" ? "usdtbsc" : "usdttrc20", data.Expected!.Value);
    internal sealed record State(Guid Id, Guid Member, Guid? Order, Guid? Product, string Method, string Status, decimal Due, string? Provider, string Origin, string? Asset,
        decimal? Expected, decimal? Tokens, decimal? Received, string? ProviderStatus, byte[]? Hash, DateTime? Confirmed, DateTime? First, DateTime? Deadline,
        string? OrderStatus, string? Kind, int Quantity, string? Fulfillment, bool LocalFirst, bool Preorder, Guid? Credit, string? ProviderCode, bool Evidence);
}
