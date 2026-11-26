using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

public sealed class SqlitePreparedCheckoutStore(SqliteSessionFactory sessions)
{
    private readonly SqliteSessionFactory sessions = sessions.RequirePlaintext();

    public Task<CheckoutResult> PurchaseAsync(BrandContext context, long actor, CheckoutRequest request,
        PreparedCheckout prepared, SqliteCatalogStore catalog, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        return PurchaseAsync(context, prepared.PolicyRevision, actor, request, prepared, catalog, ct);
    }

    public Task<CheckoutResult> PurchaseAsync(BrandContext context, long revision, long actor, CheckoutRequest request,
        PreparedCheckout prepared, SqliteCatalogStore catalog, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(catalog);
        if (revision < 0 || actor <= 0 || request.OrderId == Guid.Empty || request.ProductId == Guid.Empty ||
            request.Quantity is < 1 or > 10000 ||
            request.Payment is not (CheckoutPayment.Wallet or CheckoutPayment.Crypto or CheckoutPayment.Bank) ||
            request.Kind is not (CheckoutKind.ProductPurchase or CheckoutKind.PrepaidUnits) ||
            (request.Payment == CheckoutPayment.Crypto && request.Network is null) ||
            request.Network is CryptoAsset network && (request.Payment != CheckoutPayment.Crypto || !Enum.IsDefined(network)))
            throw new ArgumentException("The prepared checkout request is unavailable.");
        return sessions.RunAsync(true, (_, tx, token) => AcceptAsync(tx, context, revision, actor, request,
            prepared, catalog, token), ct);
    }

    private async Task<CheckoutResult> AcceptAsync(SqliteTransaction tx, BrandContext context, long revision, long actor,
        CheckoutRequest request, PreparedCheckout prepared, SqliteCatalogStore catalog, CancellationToken ct)
    {
        Guid member = await SqliteSessionFactory.StepEfAsync(tx, false, async (data, token) =>
        {
            var account = await data.RequireMembershipAsync(context, actor, false, false, token, true);
            await data.RequireRegisteredRevisionAsync(context, revision, token);
            return account!.Id;
        }, ct);
        if (prepared.Query != new CatalogQuery(request.ProductId, request.Quantity, request.Payment, request.Kind))
            throw new PricingRejectedException();
        bool localSupply = request.Kind == CheckoutKind.ProductPurchase && request.Payment == CheckoutPayment.Wallet &&
            prepared.Supply.LocalUnits == request.Quantity && prepared.Supply.RemoteUnits == 0 &&
            !prepared.Supply.WaitingForLocalStock;
        bool local = localSupply && prepared.Quote.Terms.DiscountId is null;
        bool walletCoupon = localSupply && prepared.Quote.Terms.DiscountId is not null;
        var localRequest = new LocalCheckoutRequest(request.OrderId, request.ProductId, request.Quantity,
            prepared.Quote.Terms.UnitUsdt, prepared.ListingRevision, prepared.ProductRevision, prepared.PolicyRevision);
        byte[] digest = local ? CheckoutStore.Fingerprint(context, member, localRequest) :
            PreparedFingerprint(context, member, request);
        var previous = await SqliteSessionFactory.StepEfAsync(tx, false, (data, token) => CheckoutQueries.ReplayAsync(data, request.OrderId, token), ct);
        if (!previous.NoOrder || !previous.NoAudit)
        {
            if (previous.NoOrder || previous.NoAudit || previous.Member != member || previous.Brand != context.BrandId || previous.Hash is null || !CryptographicOperations.FixedTimeEquals(previous.Hash, digest)) throw new UnauthorizedAccessException("Checkout identity was already used.");
            return new(CheckoutOutcome.Replayed, request.OrderId);
        }
        CheckoutQuote quote = await catalog.RequireCurrentAsync(tx, context, actor, prepared, ct);
        QuoteTerms terms = quote.Terms;
        if (prepared.Member != member || prepared.PolicyRevision != revision ||
            terms.Payment != request.Payment || prepared.Supply.RemoteUnits != 0 ||
            quote.Payable.Amount <= 0)
            throw new PricingRejectedException();
        if (local)
            return await new SqliteCheckoutStore(sessions).AcceptLocalAsync(
                tx, context, revision, actor, localRequest, ct);
        if (request.Payment == CheckoutPayment.Wallet && request.Kind != CheckoutKind.PrepaidUnits && !walletCoupon)
            return new(CheckoutOutcome.Unavailable);
        if (request.Payment == CheckoutPayment.Wallet &&
            (await SqliteWallet.ReadAsync(tx, member, ct)).Balance.Amount < quote.TotalUsd.Amount)
            return new(CheckoutOutcome.InsufficientBalance);
        DateTime instant = DateTime.UtcNow;
        string now = SqliteSchema.UtcText(instant);
        var pending = await SqliteSessionFactory.StepEfAsync(tx, false, async (data, token) =>
        {
            var ids = await data.Data.Set<Entities.Order>().Where(value => value.UserId == member && value.PaymentStatus == "awaiting_payment").OrderBy(value => value.Id).Select(value => value.Id).Take(1001).ToArrayAsync(token);
            if (ids.Length > 1000) data.RejectScope("Checkout pending-order bound exceeded.");
            return ids;
        }, ct);
        bool unsafePayment = await SqliteSessionFactory.StepEfAsync(tx, false, async (data, token) =>
            await data.Data.Set<Entities.Payment>().AnyAsync(value => data.Data.Set<Entities.Order>().Any(order => order.Id == value.OrderId && order.UserId == member && (order.PaymentStatus == "awaiting_payment" || order.PaymentStatus == "under_review" || order.PaymentStatus == "confirmation_pending")) && (value.FirstPaymentObservedAt != null || value.ReceiptAssetRef != null || value.PendingEvidence != null || value.Status == "observed" || value.Status == "confirming" || value.Status == "settled" || value.Status == "review"), token) ||
            await data.Data.Set<Entities.Order>().AnyAsync(value => value.UserId == member && (value.PaymentStatus == "under_review" || value.PaymentStatus == "confirmation_pending"), token) ||
            await data.Data.Set<Entities.DiscountUse>().AnyAsync(value => value.Status == "held" && data.Data.Set<Entities.Order>().Any(order => order.Id == value.OrderId && order.UserId == member && order.PaymentStatus == "awaiting_payment"), token), ct);
        if (unsafePayment) return new(CheckoutOutcome.Unavailable);
        var product = await SqliteSessionFactory.StepEfAsync(tx, false, (data, token) => data.Data.Set<Entities.Product>().Where(value => value.Id == request.ProductId && value.Active).Select(value => new { Kind = value.FulfillmentKind, value.LocalFirst, value.AvailabilityPolicy }).SingleOrDefaultAsync(token), ct);
        if (product is null || (request.Kind == CheckoutKind.ProductPurchase && (product.Kind is not ("local" or "manual") || product.AvailabilityPolicy == "local_preorder" && product.Kind != "local"))) throw new PricingRejectedException();
        string kind = product.Kind;
        bool localFirst = product.LocalFirst, preorder = request.Kind == CheckoutKind.ProductPurchase && product.AvailabilityPolicy == "local_preorder";

        List<Guid>? walletStock = null;
        if (walletCoupon)
        {
            bool older = await SqliteSessionFactory.StepEfAsync(tx, false, (data, token) => data.Data.Set<Entities.Order>().AnyAsync(value => value.ProductId == request.ProductId && value.IsPreorder && (value.PaymentStatus == "paid" || value.PaymentStatus == "not_required") && (value.FulfillmentStatus == "waiting_stock" || value.FulfillmentStatus == "requires_admin"), token), ct);
            if (older) return new(CheckoutOutcome.Unavailable);
            walletStock = await SqliteOrderPaymentSettlementStore.ReadStockAsync(tx,
                request.ProductId, request.Quantity, ct);
            if (walletStock.Count != request.Quantity) return new(CheckoutOutcome.OutOfStock);
        }

        foreach (Guid old in pending)
        {
            await SqliteSessionFactory.StepEfAsync(tx, true, async (data, token) =>
            {
                await data.RequireOrderCommandAsync(context, old, token);
                var order = await data.Data.Set<Entities.Order>().SingleAsync(value => value.Id == old && value.PaymentStatus == "awaiting_payment", token);
                order.PaymentStatus = "cancelled"; order.FulfillmentStatus = "cancelled"; order.ResolutionKind = "replaced"; order.ResolutionAt = instant; order.Revision++; order.UpdatedAt = instant;
                var jobs = await data.Data.Set<Entities.Job>().Where(value => value.OrderId == old && value.JobKind == "expire_order_payment" && (value.Status == "pending" || value.Status == "retry")).Take(1001).ToArrayAsync(token);
                if (jobs.Length > 1000) data.RejectScope("Checkout job bound exceeded.");
                foreach (var job in jobs) { job.Status = "cancelled"; job.CompletedAt = instant; }
                return true;
            }, ct);
            await QueueInstructionAsync(tx, context, member, old, "replaced", ct);
        }

        await SqliteSessionFactory.StepEfAsync(tx, true, async (data, token) =>
        {
            await CheckoutQueries.InsertAsync(data, context, actor, new Entities.Order { Id = request.OrderId, UserId = member, ProductId = request.ProductId, Quantity = request.Quantity, OrderKind = request.Kind == CheckoutKind.PrepaidUnits ? "prepaid_units" : "product_purchase", PaymentStatus = request.Payment == CheckoutPayment.Wallet ? "paid" : "awaiting_payment", Currency = terms.Currency.ToCode(), PaymentMethod = request.Payment == CheckoutPayment.Bank ? "bank" : request.Payment == CheckoutPayment.Crypto ? "crypto" : "wallet", UnitPriceUsdt = terms.UnitUsdt, UnitPriceToman = terms.UnitToman, FxRateSnapshot = terms.Rate?.Toman, FxSourceSnapshot = terms.Rate?.Source, FxFetchedAtSnapshot = terms.Rate?.FetchedAt, AutoPriceTomanSnapshot = terms.AutoToman, PricingRoundingStepSnapshot = terms.RoundingStep, PriceCalculationVersion = terms.Version, BrandPublicNameSnapshot = terms.Product.Brand, ProductTitleSnapshot = terms.Product.Title, ProductDescriptionSnapshot = terms.Product.Description, ProductIconSnapshot = terms.Product.Icon, ProductGuidanceSnapshot = terms.Product.Guidance, PaymentTermsSnapshot = "Frozen quote; complete-order delivery", FulfillmentKindSnapshot = kind, LocalFirstSnapshot = localFirst, IsPreorder = preorder, PaymentDueAt = request.Payment == CheckoutPayment.Wallet ? null : instant.AddMinutes(30), CreatedAt = instant, UpdatedAt = instant, DiscountPctSnapshot = terms.DiscountPercent, DiscountUnitsSnapshot = terms.DiscountUnits, PaymentConfirmedAt = request.Payment == CheckoutPayment.Wallet ? instant : null }, digest, token);
            data.Data.Add(new Entities.BotActivityEvent { Id = Guid.CreateVersion7(), UserId = member, EventType = "checkout", SourceKind = "flow", ActionCode = "checkout_created", ProductId = request.ProductId, OrderId = request.OrderId, QuantitySelected = request.Quantity, OutcomeCode = "accepted", IdempotencyKey = "workflow:checkout_created:" + request.OrderId.ToString("N"), OccurredAt = instant });
            return true;
        }, ct);
        if (terms.DiscountId is not null)
            await SqliteDiscountClaimStore.HoldAsync(tx, context, member, request.OrderId, request.ProductId,
                quote, ct);
        if (walletCoupon)
        {
            var wallet = await SqliteWallet.ReadAsync(tx, member, ct);
            if (wallet.Balance.Amount < quote.TotalUsd.Amount) throw new PricingRejectedException();
            await SqliteWallet.PostAsync(tx, member, wallet,
                new Money(-quote.TotalUsd.Amount, Currency.Usd), request.OrderId, false, now, ct);
            await SqliteOrderPaymentSettlementStore.AcquireAsync(
                tx, context, request.OrderId, walletStock!, now, ct);
            await OutboxStore.EnqueueSqliteAsync(tx,
                JobIntent.Order(JobKind.PrepareManifest, context.BrandId, request.OrderId),
                request.OrderId, DateTime.UtcNow, ct);
            return new(CheckoutOutcome.Purchased, request.OrderId);
        }
        if (request.Kind == CheckoutKind.PrepaidUnits && request.Payment == CheckoutPayment.Wallet)
        {
            var wallet = await SqliteWallet.ReadAsync(tx, member, ct);
            if (wallet.Balance.Amount < quote.TotalUsd.Amount)
                throw new PricingRejectedException();
            await SqliteWallet.PostAsync(tx, member, wallet,
                new Money(-quote.TotalUsd.Amount, Currency.Usd), request.OrderId, false, now, ct);
            _ = await new SqlitePrepaidSourceStore(sessions).IssueAsync(tx, context, request.OrderId, ct);
            return new(CheckoutOutcome.Purchased, request.OrderId);
        }
        await CreatePaymentAsync(tx, context, request, quote, ct);
        await OutboxStore.EnqueueSqliteAsync(tx, JobIntent.Order(JobKind.ExpireOrderPayment,
            context.BrandId, request.OrderId), request.OrderId, instant.AddMinutes(30), ct);
        await QueueInstructionAsync(tx, context, member, request.OrderId, "created", ct);
        return new(CheckoutOutcome.AwaitingPayment, request.OrderId);
    }

    private static async Task CreatePaymentAsync(SqliteTransaction tx, BrandContext context, CheckoutRequest request,
        CheckoutQuote quote, CancellationToken ct)
    {
        bool bank = request.Payment == CheckoutPayment.Bank;
        Guid payment = Guid.CreateVersion7();
        byte[]? destination = null;
        if (bank)
            destination = await SqliteSessionFactory.StepEfAsync(tx, false,
                (data, token) => CheckoutQueries.FreezeBankDestinationAsync(data, context, true, token), ct) ?? throw new PricingRejectedException();
        try
        {
            Money due = bank ? quote.TotalToman ?? throw new PricingRejectedException() : quote.TotalUsd;
            if (due.Amount <= 0) throw new PricingRejectedException();
            await SqliteSessionFactory.StepEfAsync(tx, true, async (data, token) =>
            {
                await data.RequireOrderCommandAsync(context, request.OrderId, token);
                data.Data.Add(new Entities.Payment { Id = payment, OrderId = request.OrderId, PaymentMethod = bank ? "bank" : "crypto", QuoteCurrency = due.Currency.ToCode(), AmountDue = due.Amount, IdempotencyRef = "order:" + request.OrderId.ToString("D"), NetworkAsset = bank ? null : request.Network == CryptoAsset.UsdtBsc ? "USDTBSC" : "USDTTRC20",
                    BankDestination = destination });
                return true;
            }, ct);
        }
        finally
        {
            if (destination is not null) CryptographicOperations.ZeroMemory(destination);
        }
    }

    private static async Task QueueInstructionAsync(SqliteTransaction tx, BrandContext context, Guid member,
        Guid order, string cause, CancellationToken ct)
    {
        Guid actual = await SqliteSessionFactory.StepEfAsync(tx, true, (data, token) => PaymentInstructionQueries.EnqueueAsync(data, context, order, cause, token), ct);
        await OutboxStore.EnqueueSqliteAsync(tx, JobIntent.Telegram(context.BrandId, actual), actual,
            DateTime.UtcNow, ct);
    }

    private static byte[] PreparedFingerprint(BrandContext context, Guid member, CheckoutRequest request) =>
        SHA256.HashData(Encoding.ASCII.GetBytes(
            $"prepared-goods-v1:{context.BrandId:D}:{member:D}:{request.ProductId:D}:{request.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{request.Payment}" +
            (request.Kind == CheckoutKind.PrepaidUnits ? ":prepaid" : "") +
            (request.Network is CryptoAsset network ? ":" + network : "")));

}
