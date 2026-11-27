using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

public sealed class SqliteOrderPaymentSettlementStore(SqliteSessionFactory sessions)
{
    private readonly SqliteSessionFactory sessions = sessions;

    public async Task<PaymentSettlement> SettleAsync(BrandContext context, long expectedRevision, Guid payment,
        Func<InvoiceExpectation, CancellationToken, Task<ProviderPaymentSnapshot>> lookup, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        var before = await sessions.RunAsync(false, (_, tx, token) => LoadAsync(tx, context, expectedRevision, payment, token), ct);
        if (before.Status == "settled") return Replayed(before);
        if (!Ready(before)) return Incomplete(before);
        if (before.Kind != "product_purchase" || before.Fulfillment is not ("local" or "manual"))
            throw new NotSupportedException("This SQLite settlement route has not been qualified.");
        var invoice = Expectation(before);
        ProviderPaymentSnapshot remote = await lookup(invoice, ct);
        return await sessions.RunAsync(true, async (_, tx, token) =>
        {
            var current = await LoadAsync(tx, context, expectedRevision, payment, token);
            if (current.Status == "settled") return Replayed(current);
            if (!Ready(current)) return Incomplete(current);
            var unchanged = before with { Hash = current.Hash };
            if (current != unchanged || current.Hash is null || before.Hash is null ||
                !CryptographicOperations.FixedTimeEquals(current.Hash, before.Hash) ||
                Stopwatch.GetElapsedTime(remote.ObservedAt) > TimeSpan.FromSeconds(30))
                throw new PaymentNoticeRejectedException();
            bool sameInvoice = remote.Invoice == invoice;
            if (!sameInvoice || remote.Received != invoice.ExpectedTokens || remote.Status != "finished")
            {
                if (sameInvoice && remote.Received <= invoice.ExpectedTokens &&
                    remote.Status is ("waiting" or "confirming" or "confirmed" or "sending"))
                    return new PaymentSettlement(PaymentSettlementOutcome.Pending, ExternalSettlementRoute.Review);
                await SqlitePaymentOperations.PaymentAsync(tx, context, payment, row => { if (row.Status != "confirming") throw new PaymentNoticeRejectedException(); row.Status = "review"; }, token);
                return new(PaymentSettlementOutcome.Review, ExternalSettlementRoute.Review);
            }

            DateTime instant = await SqliteSessionFactory.StepEfAsync(tx, false, (data, cancel) => data.BrandClockAsync(context.BrandId, cancel), token);
            string now = SqliteSchema.UtcText(instant);
            await SqlitePaymentOperations.PaymentAsync(tx, context, payment, row => { if (row.Status != "confirming") throw new PaymentNoticeRejectedException(); row.Status = "settled"; }, token);
            Guid? credit = null;
            ExternalSettlementRoute route;
            bool late = current.OrderStatus is ("expired" or "cancelled");
            if (!late && current.First is not null && current.Deadline is not null &&
                current.First > current.Deadline &&
                current.OrderStatus is ("awaiting_payment" or "under_review" or "confirmation_pending"))
            {
                await SqlitePaymentOperations.OrderAsync(tx, current.Order, row => { row.PaymentStatus = "expired"; row.FulfillmentStatus = "cancelled"; row.ResolutionKind = "unpaid_expiry"; row.ResolutionAt = instant; }, instant, token);
                late = true;
            }
            if (late)
            {
                route = ExternalSettlementRoute.LateCredit;
                credit = await CreditAsync(tx, current, "late_crypto_credit", now, token);
                await SqliteDiscountClaimStore.TransitionAsync(tx, context, current.Order, false, token);
            }
            else if (current.OrderStatus is ("awaiting_payment" or "under_review" or "confirmation_pending"))
            {
                await SqlitePaymentOperations.OrderAsync(tx, current.Order, row => { row.PaymentStatus = "paid"; row.PaymentConfirmedAt = current.Confirmed; }, instant, token);
                if (current.Kind == "prepaid_units")
                {
                    route = ExternalSettlementRoute.Prepaid;
                    await new SqlitePrepaidSourceStore(sessions)
                        .IssueAsync(tx, context, current.Order, token);
                }
                else if (current.Kind != "product_purchase")
                    throw new UnauthorizedAccessException("The original order kind is unavailable.");
                else if (current.Preorder)
                {
                    route = ExternalSettlementRoute.LocalPreorder;
                    await FulfillmentAsync(tx, current.Order, "waiting_stock", null, now, token);
                    await OutboxStore.EnqueueSqliteAsync(tx, JobIntent.Product(JobKind.DrainLocalPreorders, current.Product),
                        current.Order, DateTime.UtcNow, token);
                }
                else if (current.Fulfillment == "manual")
                {
                    route = ExternalSettlementRoute.LocalGoods;
                    await FulfillmentAsync(tx, current.Order, "requires_admin", null, now, token);
                }
                else
                {
                    var stock = await ReadStockAsync(tx, current.Product, current.Quantity, token);
                    if (stock.Count != current.Quantity)
                    {
                        route = ExternalSettlementRoute.LocalShortageCredit;
                        credit = await CreditAsync(tx, current, "immediate_crypto_shortage_credit", now, token);
                        await SqlitePaymentOperations.OrderAsync(tx, current.Order, row => { row.FulfillmentStatus = "compensated"; row.ResolutionKind = "immediate_crypto_shortage_credit"; row.ResolutionAt = instant; row.ResolutionWalletEntryId = credit; }, instant, token);
                        await SqliteDiscountClaimStore.TransitionAsync(tx, context, current.Order, false, token);
                    }
                    else
                    {
                        route = ExternalSettlementRoute.LocalGoods;
                        await AcquireAsync(tx, context, current.Order, stock, now, token);
                        await FulfillmentAsync(tx, current.Order, "acquiring", null, now, token);
                        await OutboxStore.EnqueueSqliteAsync(tx,
                            JobIntent.Order(JobKind.PrepareManifest, context.BrandId, current.Order), current.Order, DateTime.UtcNow, token);
                    }
                }
            }
            else throw new PaymentNoticeRejectedException();

            await SqlitePaymentOperations.CancelExpiryAsync(tx, context, current.Order, instant, token);
            if (credit is Guid entry) await SqlitePaymentOperations.PaymentAsync(tx, context, payment, row => row.CompensatingWalletEntryId = entry, token);
            await QueueStatusAsync(tx, context, current, token);
            return new(PaymentSettlementOutcome.Settled, route, credit);
        }, ct);
    }

    private static async Task<State> LoadAsync(SqliteTransaction tx, BrandContext context, long revision, Guid payment, CancellationToken ct)
    {
        var state = await SqliteSessionFactory.StepEfAsync(tx, false, (data, token) =>
            (from value in data.Data.Set<Entities.Payment>()
             join order in data.Data.Set<Entities.Order>() on value.OrderId equals order.Id
             join user in data.Data.Set<Entities.User>() on order.UserId equals user.Id
             join brand in data.Data.Set<Entities.Brand>() on user.BrandId equals brand.Id
             where value.Id == payment && brand.Id == context.BrandId && brand.BotId == context.BotId && brand.Active && brand.PolicyVersion == revision && value.QuoteCurrency == "USD"
             select new State(user.Id, order.Id, order.ProductId!.Value, value.Status, order.PaymentStatus, order.FulfillmentKindSnapshot, order.IsPreorder, order.Quantity, order.OrderKind, value.PaymentMethod, value.AmountDue, value.ExpectedNetworkAmount, value.NetworkAmount, value.AmountReceived, value.ProviderPaymentRef, value.IdempotencyRef, value.NetworkAsset, value.ProviderStatusCode, value.ProviderCode, value.IpnPayloadSha256, value.EvidenceAssetRef != null, value.ConfirmedAt, value.FirstPaymentObservedAt, order.PaymentDueAt, value.CompensatingWalletEntryId, value.Id)).SingleOrDefaultAsync(token), ct);
        return state ?? throw new UnauthorizedAccessException("Payment is unavailable.");
    }

    private static bool Ready(State state) =>
        state.Method == "crypto" && state.Status == "confirming" && state.ProviderCode == "nowpayments" &&
        state.Provider is not null && state.ProviderStatus == "finished" && state.Expected is > 0 &&
        state.Tokens == state.Expected && state.Received == state.Due && state.Hash is not null &&
        state.Evidence && state.Confirmed is not null && state.First is not null &&
        state.Asset is ("USDTBSC" or "USDTTRC20");

    private static InvoiceExpectation Expectation(State data) =>
        new(data.Provider!, data.Origin, data.Due, data.Asset == "USDTBSC" ? "usdtbsc" : "usdttrc20", data.Expected!.Value);

    private static PaymentSettlement Incomplete(State state) => new(
        state.Status == "review" ? PaymentSettlementOutcome.Review : PaymentSettlementOutcome.Pending, ExternalSettlementRoute.Review);
    private static PaymentSettlement Replayed(State state) =>
        new(PaymentSettlementOutcome.Replayed, ExternalSettlementRoute.AlreadyAccepted, state.Credit);

    private static async Task<Guid> CreditAsync(SqliteTransaction tx, State state, string kind, string now, CancellationToken ct)
    {
        return await SqliteSessionFactory.StepEfAsync(tx, true, (data, token) => WalletOperations.PostAsync(data, state.Member, ExternalPaymentPolicy.FrozenCredit(state.Due), null,
            new Entities.WalletEntry { Id = Guid.CreateVersion7(), MovementKind = kind, IdempotencyKey = "payment-credit:" + state.PaymentId.ToString("D"), OrderId = state.Order, PaymentId = state.PaymentId, Reason = "verified_original_invoice" }, token, DateTime.ParseExact(now, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal)), ct);
    }

    internal static async Task AcquireAsync(SqliteTransaction tx, BrandContext context, Guid order,
        IReadOnlyList<Guid> stock, string now, CancellationToken ct)
    {
        int unit = 0;
        foreach (var item in stock)
        {
            Guid fulfillment = Guid.CreateVersion7();
            int number = ++unit;
            await SqliteSessionFactory.StepEfAsync(tx, true, async (data, token) =>
            {
                await data.RequireOrderCommandAsync(context, order, token);
                await CheckoutQueries.AcquireStockAsync(data, order, item, fulfillment, number, Utc(now), token);
                return true;
            }, ct);
        }
    }

    internal static Task<List<Guid>> ReadStockAsync(SqliteTransaction tx, Guid product, int quantity, CancellationToken ct) =>
        SqliteSessionFactory.StepEfAsync(tx, false, (data, token) => ReadStockAsync(data, product, quantity, token), ct);

    internal static Task<List<Guid>> ReadStockAsync(EfSession data, Guid product, int quantity, CancellationToken ct)
    {
        var rows = data.Data.Set<Entities.InventoryItem>().Where(value => value.ProductId == product && value.State == "available").OrderBy(value => value.Id).Take(quantity);
        return rows.Select(row => row.Id).ToListAsync(ct);
    }

    private static Task<bool> FulfillmentAsync(SqliteTransaction tx, Guid order, string status, string? review, string now, CancellationToken ct) => SqlitePaymentOperations.OrderAsync(tx, order, row => { row.FulfillmentStatus = status; row.ReviewCode = review; }, Utc(now), ct);
    private static async Task QueueStatusAsync(SqliteTransaction tx, BrandContext context, State state, CancellationToken ct)
    {
        Guid delivery = await SqlitePaymentOperations.StatusAsync(tx, context, state.Order, state.Member, "payment-status:" + state.PaymentId.ToString("D"), ct);
        await OutboxStore.EnqueueSqliteAsync(tx, JobIntent.Telegram(context.BrandId, delivery), state.PaymentId, DateTime.UtcNow, ct);
    }
    private static DateTime Utc(string value) => DateTime.ParseExact(value, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);

    private sealed record State(Guid Member, Guid Order, Guid Product, string Status, string OrderStatus, string Fulfillment,
        bool Preorder, int Quantity, string Kind, string Method, decimal Due, decimal? Expected, decimal? Tokens,
        decimal? Received, string? Provider, string Origin, string? Asset, string? ProviderStatus, string? ProviderCode,
        byte[]? Hash, bool Evidence, DateTime? Confirmed, DateTime? First, DateTime? Deadline, Guid? Credit, Guid PaymentId);
}
