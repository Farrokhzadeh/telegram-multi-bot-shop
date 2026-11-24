using Microsoft.EntityFrameworkCore;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

public sealed record CheckoutRequest(Guid OrderId, Guid ProductId, int Quantity, CheckoutPayment Payment = CheckoutPayment.Wallet, CheckoutKind Kind = CheckoutKind.ProductPurchase, CryptoAsset? Network = null);

public sealed partial class CheckoutStore
{
    public async Task<CheckoutResult?> ReplayAsync(BrandContext context, long actor, CheckoutRequest request, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginAsync(ct);
        return await ReplayLockedAsync(session, context, actor, request, ct);
    }

    public async Task<CheckoutResult?> ReplayForBrandAsync(BrandContext context, long expectedRevision, long actor, CheckoutRequest request, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        await using var session = await sessions.BeginAsync(ct);
        return await ReplayLockedAsync(session, context, actor, request, ct, expectedRevision);
    }
    internal async Task<CheckoutResult?> ReplayLockedAsync(DbSession session, BrandContext context, long actor, CheckoutRequest request, CancellationToken ct, long? writerRevision = null)
    {
        if (writerRevision is < 0) throw new ArgumentOutOfRangeException(nameof(writerRevision));
        if (request.OrderId == Guid.Empty || request.ProductId == Guid.Empty || request.Quantity is < 1 or > 10000 ||
            request.Payment is not (CheckoutPayment.Wallet or CheckoutPayment.Crypto or CheckoutPayment.Bank) || !Enum.IsDefined(request.Kind) ||
            (request.Network is not null && (request.Payment != CheckoutPayment.Crypto || !Enum.IsDefined(request.Network.Value)))) throw new ArgumentException("Invalid checkout request.");
        await session.RunEfStepAsync(false, (data, token) => data.LockMemberAsync(context.BrandId, actor, token), ct);
        BrandUser member = await BrandUserStore.RequireAsync(session, context, actor, null, ct);
        if (member.Blocked || member.Banned) Reject(session);
        await session.ExecuteAsync(new DbStatement("SELECT pg_advisory_xact_lock($1)", DbValue.Bigint(BinaryPrimitives.ReadInt64BigEndian(request.OrderId.ToByteArray(bigEndian: true)))), ct);
        if (writerRevision is long revision) await BrandWriterStore.RequireCustomerAsync(session, context, actor, revision, request.ProductId, ct);
        else await session.AdmitCustomerWriterAsync(context, actor, request.ProductId, ct);
        var prior = await session.RunEfStepAsync(false, (data, token) => CheckoutQueries.ReplayAsync(data, request.OrderId, token), ct);
        if (prior.NoOrder && prior.NoAudit) return null;
        if (prior.NoOrder || prior.NoAudit || prior.Member != member.Id || prior.Brand != context.BrandId || prior.Hash is null ||
            !CryptographicOperations.FixedTimeEquals(prior.Hash, Fingerprint(context, member.Id, request))) Reject(session);
        return new(CheckoutOutcome.Replayed, request.OrderId);
    }

    internal async Task<CheckoutResult> AcceptPreparedAsync(DbSession session, BrandContext context, long actor, CheckoutRequest request,
        PreparedCheckout prepared, CatalogStore catalog, CancellationToken ct)
    {
        if (prepared.Query != new CatalogQuery(request.ProductId, request.Quantity, request.Payment, request.Kind)) Reject(session);
        CheckoutQuote quote = prepared.Quote;
        QuoteTerms terms = quote.Terms;
        bool free = terms.Payment == CheckoutPayment.NoPaymentRequired;
        bool unpaid = terms.Payment is CheckoutPayment.Crypto or CheckoutPayment.Bank;
        if (!free && quote.Payable.Amount <= 0) throw new PricingRejectedException();
        if (request.Kind == CheckoutKind.PrepaidUnits && free) return new(CheckoutOutcome.Unavailable);
        var product = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.Product>().Where(value => value.Id == request.ProductId).Select(value => new { Kind = value.FulfillmentKind, value.LocalFirst, Preorder = value.AvailabilityPolicy == "local_preorder" }).SingleAsync(token), ct);
        var pending = await session.RunEfStepAsync(false, async (data, token) =>
        {
            await data.LockPendingOrdersAsync(prepared.Member, token);
            return await data.Data.Set<Entities.Order>().Where(value => value.UserId == prepared.Member && value.PaymentStatus == "awaiting_payment").OrderBy(value => value.Id).Select(value => value.Id).Take(1001).ToArrayAsync(token);
        }, ct);
        bool evidence = await session.RunEfStepAsync(false, async (data, token) =>
        {
            await data.LockPendingPaymentsAsync(prepared.Member, token);
            return await data.Data.Set<Entities.Payment>().AnyAsync(value => data.Data.Set<Entities.Order>().Any(order => order.Id == value.OrderId && order.UserId == prepared.Member && order.PaymentStatus == "awaiting_payment") && (value.FirstPaymentObservedAt != null || value.ReceiptAssetRef != null || value.Status == "observed" || value.Status == "confirming" || value.Status == "settled" || value.Status == "review"), token);
        }, ct);
        if (evidence) return new(CheckoutOutcome.Unavailable);
        var wallet = await WalletOperations.ReadAsync(session, prepared.Member, ct);
        if (!free && !unpaid && wallet.Balance.Amount < quote.TotalUsd.Amount) return new(CheckoutOutcome.InsufficientBalance);
        int local = unpaid || request.Kind == CheckoutKind.PrepaidUnits ? 0 : prepared.Supply.LocalUnits;
        var stock = await ReadStockAsync(session, request.ProductId, local, ct);
        if (stock.Count != local) return new(CheckoutOutcome.OutOfStock);
        if (System.Diagnostics.Stopwatch.GetElapsedTime(prepared.IssuedAt) > TimeSpan.FromSeconds(30)) Reject(session);
        var pricing = new PricingStore(sessions, secrets);
        foreach (var old in pending)
        {
            await session.RunEfStepAsync(true, async (data, token) =>
            {
                await data.RequireOrderCommandAsync(context, old, token);
                var order = await data.Data.Set<Entities.Order>().SingleAsync(value => value.Id == old, token);
                DateTime now = await data.BrandClockAsync(context.BrandId, token);
                order.PaymentStatus = "cancelled"; order.FulfillmentStatus = "cancelled"; order.ResolutionKind = "replaced"; order.ResolutionAt = now; order.Revision++; order.UpdatedAt = now;
                return true;
            }, ct);
            Guid? held = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.DiscountUse>().Where(value => value.OrderId == old && value.Status == "held").Select(value => (Guid?)value.DiscountId).SingleOrDefaultAsync(token), ct);
            if (held is Guid discount) await pricing.TransitionClaimAsync(session, context, discount, old, false, ct);
            await session.RunEfStepAsync(true, async (data, token) =>
            {
                await data.RequireOrderCommandAsync(context, old, token);
                await data.LockExpiryJobsAsync(old, token);
                var jobs = await data.Data.Set<Entities.Job>().Where(value => value.OrderId == old && value.JobKind == "expire_order_payment" && (value.Status == "pending" || value.Status == "retry")).Take(1001).ToArrayAsync(token);
                if (jobs.Length > 1000) data.RejectScope("Checkout job bound exceeded.");
                DateTime now = await data.BrandClockAsync(context.BrandId, token);
                foreach (var job in jobs) { job.Status = "cancelled"; job.CompletedAt = now; }
                return true;
            }, ct);
            await PaymentInstructionStore.EnqueueAsync(session, context, old, "replaced", ct);
        }
        await session.RunEfStepAsync(true, async (data, token) =>
        {
            DateTime now = await data.BrandClockAsync(context.BrandId, token);
            await CheckoutQueries.InsertAsync(data, context, actor, new Entities.Order { Id = request.OrderId, UserId = prepared.Member, ProductId = request.ProductId, Quantity = request.Quantity, OrderKind = request.Kind == CheckoutKind.PrepaidUnits ? "prepaid_units" : "product_purchase", PaymentStatus = unpaid ? "awaiting_payment" : free ? "not_required" : "paid", Currency = terms.Currency.ToCode(), PaymentMethod = free ? "no_payment_required" : terms.Payment.ToString().ToLowerInvariant(), UnitPriceUsdt = terms.UnitUsdt, UnitPriceToman = terms.UnitToman, FxRateSnapshot = terms.Rate?.Toman, FxSourceSnapshot = terms.Rate?.Source, FxFetchedAtSnapshot = terms.Rate?.FetchedAt, AutoPriceTomanSnapshot = terms.AutoToman, PricingRoundingStepSnapshot = terms.RoundingStep, PriceCalculationVersion = terms.Version, DiscountPctSnapshot = terms.DiscountPercent, DiscountUnitsSnapshot = terms.DiscountUnits, BrandPublicNameSnapshot = terms.Product.Brand, ProductTitleSnapshot = terms.Product.Title, ProductDescriptionSnapshot = terms.Product.Description, ProductIconSnapshot = terms.Product.Icon, ProductGuidanceSnapshot = terms.Product.Guidance, PaymentTermsSnapshot = "Frozen quote; complete-order delivery", FulfillmentKindSnapshot = product.Kind, LocalFirstSnapshot = product.LocalFirst, IsPreorder = request.Kind == CheckoutKind.ProductPurchase && product.Preorder, PaymentConfirmedAt = unpaid ? null : now, FulfillmentStatus = unpaid || request.Kind == CheckoutKind.PrepaidUnits ? "unfulfilled" : prepared.Supply.WaitingForLocalStock ? "waiting_stock" : product.Kind == "manual" ? "requires_admin" : "acquiring", PaymentDueAt = unpaid ? now.AddMinutes(30) : null, CreatedAt = now, UpdatedAt = now }, Fingerprint(context, prepared.Member, request), token);
            return true;
        }, ct);
        await BotActivityWriter.CheckoutAsync(session, context, actor, request.OrderId, ct);
        if (terms.DiscountId is Guid coupon) await pricing.HoldAsync(session, context, actor, coupon, request.OrderId, ct);
        if (!free && !unpaid)
        {
            await WalletOperations.PostAsync(session, prepared.Member, new Money(-quote.TotalUsd.Amount, Currency.Usd), wallet.Version,
                new Entities.WalletEntry { Id = Guid.CreateVersion7(), MovementKind = "purchase_debit", IdempotencyKey = "checkout:" + request.OrderId.ToString("D"), OrderId = request.OrderId, Reason = "customer_request" }, ct);
        }
        if (unpaid)
        {
            await CreatePaymentAsync(session, context, request, quote, ct);
            DateTime due = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.Order>().Where(value => value.Id == request.OrderId).Select(value => value.PaymentDueAt!.Value).SingleAsync(token), ct);
            await OutboxStore.EnqueueAsync(session, JobIntent.Order(JobKind.ExpireOrderPayment, context.BrandId, request.OrderId), request.OrderId, due, ct);
            await PaymentInstructionStore.EnqueueAsync(session, context, request.OrderId, "created", ct);
            return new(CheckoutOutcome.AwaitingPayment, request.OrderId);
        }
        if (request.Kind == CheckoutKind.PrepaidUnits)
        {
            await pricing.IssuePaidSourceAsync(session, context, request.OrderId, ct);
            return new(CheckoutOutcome.Purchased, request.OrderId);
        }
        if (prepared.Supply.RemoteUnits > 0) await catalog.BindSupplierAsync(session, context, prepared, request.OrderId, ct);
        await AcquireAsync(session, context, request.OrderId, stock, ct);
        if (prepared.Supply.RemoteUnits == 0 && local == request.Quantity)
        {
            if (!product.Preorder && terms.DiscountId is Guid discount) await pricing.TransitionClaimAsync(session, context, discount, request.OrderId, true, ct);
            await OutboxStore.EnqueueAsync(session, JobIntent.Order(JobKind.PrepareManifest, context.BrandId, request.OrderId), request.OrderId, DateTime.UtcNow, ct);
        }
        else if (prepared.Supply.WaitingForLocalStock) await OutboxStore.EnqueueAsync(session, JobIntent.Product(JobKind.DrainLocalPreorders, request.ProductId), request.OrderId, DateTime.UtcNow, ct);
        return new(CheckoutOutcome.Purchased, request.OrderId);
    }

    internal static async Task<List<Stock>> ReadStockAsync(DbSession session, Guid product, int wanted, CancellationToken ct)
    {
        var stock = new List<Stock>(wanted);
        while (stock.Count < wanted)
        {
            int batch = Math.Min(1000, wanted - stock.Count);
            var rows = await session.RunEfStepAsync(false, async (data, token) =>
            {
                await data.LockInventoryAsync(product, stock.Count, batch, token);
                return await data.Data.Set<Entities.InventoryItem>().Where(value => value.ProductId == product && value.State == "available").OrderBy(value => value.Id).Skip(stock.Count).Take(batch).Select(value => new Stock(value.Id, value.CredentialCiphertext, value.CredentialKeyId)).ToListAsync(token);
            }, ct);
            stock.AddRange(rows);
            if (rows.Count != batch) break;
        }
        return stock;
    }
    internal async Task AcquireAsync(DbSession session, BrandContext context, Guid order, IReadOnlyList<Stock> stock, CancellationToken ct)
    {
        var inventory = new InventoryCipher(secrets);
        var cipher = new BrandCipher(secrets);
        int unit = 0;
        foreach (Stock item in stock)
        {
            Guid fulfillment = Guid.CreateVersion7();
            byte[] plain = inventory.Decrypt(item.Id, item.Key, item.Content);
            byte[] encrypted;
            try { encrypted = cipher.Encrypt(context, "fulfillments", fulfillment, "delivered_content", plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            int number = ++unit;
            await session.RunEfStepAsync(true, async (data, token) =>
            {
                await data.RequireOrderCommandAsync(context, order, token);
                var content = new Entities.Fulfillment { Id = fulfillment, OrderId = order, UnitNo = number, Kind = "local" };
                data.Data.Add(content);
                await data.FlushAsync(token, false);
                var inventoryItem = await data.Data.Set<Entities.InventoryItem>().SingleAsync(value => value.Id == item.Id && value.State == "available", token);
                inventoryItem.State = "allocated"; inventoryItem.AssignedFulfillmentId = fulfillment; inventoryItem.AllocatedAt = await data.BrandClockAsync(context.BrandId, token);
                await data.FlushAsync(token, false);
                content.Status = "acquired"; content.DeliveredContent = encrypted; content.DeliveredContentKeyId = secrets.CurrentBrandKeyId(context); content.ContentKind = "text";
                return true;
            }, ct);
        }
    }
    private static byte[] Fingerprint(BrandContext context, Guid member, CheckoutRequest request) => SHA256.HashData(Encoding.ASCII.GetBytes(
        $"prepared-goods-v1:{context.BrandId:D}:{member:D}:{request.ProductId:D}:{request.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{request.Payment}" + (request.Kind == CheckoutKind.PrepaidUnits ? ":prepaid" : "") + (request.Network is CryptoAsset network ? ":" + network : "")));
}
