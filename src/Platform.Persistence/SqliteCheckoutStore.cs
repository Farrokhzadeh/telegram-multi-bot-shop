using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Platform.Core;
using Platform.Persistence.Configurations;
using Platform.Security;

namespace Platform.Persistence;

public sealed class SqliteCheckoutStore(SqliteSessionFactory sessions)
{
    private readonly SqliteSessionFactory sessions = sessions.RequirePlaintext();

    public Task<CheckoutResult> PurchaseLocalAsync(BrandContext context, long expectedRevision, long actor, LocalCheckoutRequest request, CancellationToken ct) => sessions.RunAsync(true, (_, tx, token) => AcceptLocalAsync(tx, context, expectedRevision, actor, request, token), ct);
    internal async Task<CheckoutResult> AcceptLocalAsync(SqliteTransaction tx, BrandContext context, long expectedRevision, long actor, LocalCheckoutRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(request);
        if (actor <= 0 || expectedRevision < 0 || request.OrderId == Guid.Empty || request.ProductId == Guid.Empty || request.Quantity is < 1 or > 10000 || request.UnitPrice <= 0 || request.ListingRevision < 0 || request.ProductRevision < 0 || request.PolicyRevision < 0) throw new ArgumentException("Invalid checkout request.");
        var result = await SqliteSessionFactory.StepEfAsync(tx, true, async (data, token) =>
        {
            var member = (await data.RequireMembershipAsync(context, actor, false, false, token, true))!;
            await data.RequireRegisteredRevisionAsync(context, expectedRevision, token);
            byte[] proof = CheckoutStore.Fingerprint(context, member.Id, request);
            var prior = await CheckoutQueries.ReplayAsync(data, request.OrderId, token);
            if (!prior.NoOrder || !prior.NoAudit)
            {
                if (prior.NoOrder || prior.NoAudit || prior.Member != member.Id || prior.Brand != context.BrandId || prior.Hash is null || !CryptographicOperations.FixedTimeEquals(prior.Hash, proof)) data.RejectScope("Checkout request is unavailable.");
                return new CheckoutResult(CheckoutOutcome.Replayed, request.OrderId);
            }
            var state = await CatalogQueries.LoadAsync(data, context, actor, request.ProductId, token);
            if (state is null || state.Kind != "local" || state.Policy != "immediate" || !state.Wallet || request.Quantity < state.Minimum || request.Quantity > state.Maximum) return new(CheckoutOutcome.Unavailable);
            if (state.UnitUsdt != request.UnitPrice || state.ListingRevision != request.ListingRevision || state.ProductRevision != request.ProductRevision || expectedRevision != request.PolicyRevision) return new(CheckoutOutcome.QuoteChanged);
            if (await data.Data.Set<Entities.Order>().AnyAsync(value => value.UserId == member.Id && (value.PaymentStatus == "awaiting_payment" || value.PaymentStatus == "under_review" || value.PaymentStatus == "confirmation_pending"), token) ||
                await data.Data.Set<Entities.Order>().AnyAsync(value => value.ProductId == request.ProductId && value.IsPreorder && (value.PaymentStatus == "paid" || value.PaymentStatus == "not_required") && (value.FulfillmentStatus == "waiting_stock" || value.FulfillmentStatus == "requires_admin"), token) ||
                await data.Data.Set<Entities.Discount>().AnyAsync(value => value.BrandId == context.BrandId && value.Active && (value.ProductId == null || value.ProductId == request.ProductId) && (value.EligibleUserId == null || value.EligibleUserId == member.Id) && (value.StartsAt == null || value.StartsAt <= EfValues.UtcNow()) && (value.EndsAt == null || value.EndsAt > EfValues.UtcNow()), token)) return new(CheckoutOutcome.Unavailable);
            Money total = new Money(state.UnitUsdt, Currency.Usd).MultiplyQuantity(request.Quantity);
            var wallet = await WalletOperations.ReadAsync(data, member.Id, token);
            if (wallet.Balance.Amount < total.Amount) return new(CheckoutOutcome.InsufficientBalance);
            var stock = await SqliteOrderPaymentSettlementStore.ReadStockAsync(data, request.ProductId, request.Quantity, token);
            if (stock.Count != request.Quantity) return new CheckoutResult(CheckoutOutcome.OutOfStock);
            DateTime now = await data.BrandClockAsync(context.BrandId, token);
            var product = state.Presentation;
            await CheckoutQueries.InsertAsync(data, context, actor, new Entities.Order { Id = request.OrderId, UserId = member.Id, ProductId = request.ProductId, Quantity = request.Quantity, OrderKind = "product_purchase", PaymentStatus = "paid", Currency = "USDT", PaymentMethod = "wallet", UnitPriceUsdt = state.UnitUsdt, AutoPriceTomanSnapshot = false, PricingRoundingStepSnapshot = 5000m, PriceCalculationVersion = "local-wallet-v1", BrandPublicNameSnapshot = product.Brand, ProductTitleSnapshot = product.Title, ProductDescriptionSnapshot = product.Description, ProductIconSnapshot = product.Icon, ProductGuidanceSnapshot = product.Guidance, PaymentTermsSnapshot = "USD store credit; complete-order delivery", FulfillmentKindSnapshot = "local", LocalFirstSnapshot = state.LocalFirst, PaymentConfirmedAt = now, CreatedAt = now, UpdatedAt = now }, proof, token);
            data.Data.Add(new Entities.BotActivityEvent { Id = Guid.NewGuid(), UserId = member.Id, EventType = "checkout", SourceKind = "flow", ActionCode = "checkout_created", ProductId = request.ProductId, OrderId = request.OrderId, QuantitySelected = request.Quantity, OutcomeCode = "accepted", IdempotencyKey = $"workflow:checkout_created:{request.OrderId:N}", OccurredAt = now });
            await data.FlushAsync(token, false);
            await WalletOperations.PostAsync(data, member.Id, new Money(-total.Amount, Currency.Usd), wallet.Version, new Entities.WalletEntry { Id = Guid.CreateVersion7(), MovementKind = "purchase_debit", IdempotencyKey = $"checkout:{request.OrderId:D}", OrderId = request.OrderId, Reason = "customer_request" }, token, now);
            await data.FlushAsync(token, false);
            int unit = 0;
            foreach (var item in stock)
            {
                await CheckoutQueries.AcquireStockAsync(data, request.OrderId, item,
                    Guid.CreateVersion7(), ++unit, now, token);
                data.Data.ChangeTracker.Clear();
            }
            return new CheckoutResult(CheckoutOutcome.Purchased, request.OrderId);
        }, ct);
        if (result.Outcome == CheckoutOutcome.Purchased) await OutboxStore.EnqueueSqliteAsync(tx, JobIntent.Order(JobKind.PrepareManifest, context.BrandId, request.OrderId), request.OrderId, DateTime.UtcNow, ct);
        return result;
    }
}
