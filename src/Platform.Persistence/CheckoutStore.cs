using Microsoft.EntityFrameworkCore;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Platform.Core;
using Platform.Persistence.Configurations;
using Platform.Security;

namespace Platform.Persistence;

public enum CheckoutOutcome { Purchased, Replayed, Unavailable, QuoteChanged, InsufficientBalance, OutOfStock, AwaitingPayment }
public sealed record LocalCheckoutRequest(Guid OrderId, Guid ProductId, int Quantity, decimal UnitPrice, long ListingRevision = 0, long ProductRevision = 0, long PolicyRevision = 0);
public sealed record CheckoutResult(CheckoutOutcome Outcome, Guid? OrderId = null);

public sealed partial class CheckoutStore(DbSessionFactory sessions, SecretSnapshot secrets)
{
    public async Task<CheckoutResult> PurchaseLocalAsync(BrandContext context, long verifiedActorId, LocalCheckoutRequest request, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginAsync(ct);
        CheckoutResult result = await AcceptLocalAsync(session, context, verifiedActorId, request, ct);
        await session.CommitAsync(ct);
        return result;
    }

    public async Task<CheckoutResult> PurchaseLocalForBrandAsync(BrandContext context, long expectedRevision, long verifiedActorId, LocalCheckoutRequest request, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginAsync(ct);
        var result = await AcceptLocalForBrandAsync(session, context, expectedRevision, verifiedActorId, request, ct);
        await session.CommitAsync(ct);
        return result;
    }
    public Task<CheckoutResult> AcceptLocalAsync(DbSession session, BrandContext context, long verifiedActorId, LocalCheckoutRequest request, CancellationToken ct = default)
        => AcceptLocalCoreAsync(session, context, verifiedActorId, request, null, ct);
    public Task<CheckoutResult> AcceptLocalForBrandAsync(DbSession session, BrandContext context, long expectedRevision, long verifiedActorId, LocalCheckoutRequest request, CancellationToken ct = default)
        => AcceptLocalCoreAsync(session, context, verifiedActorId, request, expectedRevision, ct);
    private async Task<CheckoutResult> AcceptLocalCoreAsync(DbSession session, BrandContext context, long verifiedActorId, LocalCheckoutRequest request, long? writerRevision, CancellationToken ct)
    {
        try
        {
            if (writerRevision is < 0) throw new ArgumentOutOfRangeException(nameof(writerRevision));
            CheckoutResult result = await AcceptCoreAsync(session, context, verifiedActorId, request, writerRevision, ct);
            if (result.Outcome is not (CheckoutOutcome.Purchased or CheckoutOutcome.Replayed))
                await BotActivityWriter.CheckoutRejectedAsync(session, context, verifiedActorId, request.ProductId, request.Quantity, ct);
            return result;
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }

    private async Task<CheckoutResult> AcceptCoreAsync(DbSession session, BrandContext context, long verifiedActorId, LocalCheckoutRequest request, long? writerRevision, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.OrderId == Guid.Empty || request.ProductId == Guid.Empty || request.Quantity is < 1 or > 10000 || request.UnitPrice <= 0 ||
            request.ListingRevision < 0 || request.ProductRevision < 0 || request.PolicyRevision < 0) throw new ArgumentException("Invalid checkout request.");
        await session.RunEfStepAsync(false, (data, token) => data.LockMemberAsync(context.BrandId, verifiedActorId, token), ct);
        BrandUser member = await BrandUserStore.RequireAsync(session, context, verifiedActorId, null, ct);
        if (member.Blocked || member.Banned) Reject(session);
        await session.ExecuteAsync(new DbStatement("SELECT pg_advisory_xact_lock($1)", DbValue.Bigint(BinaryPrimitives.ReadInt64BigEndian(request.OrderId.ToByteArray(bigEndian: true)))), ct);
        if (writerRevision is long revision) await BrandWriterStore.RequireCustomerAsync(session, context, verifiedActorId, revision, request.ProductId, ct);
        else await session.AdmitCustomerWriterAsync(context, verifiedActorId, request.ProductId, ct);
        byte[] fingerprint = Fingerprint(context, member.Id, request);
        var prior = await session.RunEfStepAsync(false, (data, token) => CheckoutQueries.ReplayAsync(data, request.OrderId, token), ct);
        if (!prior.NoOrder || !prior.NoAudit)
        {
            if (prior.NoOrder || prior.NoAudit || prior.Member != member.Id || prior.Brand != context.BrandId || prior.Hash is null || !CryptographicOperations.FixedTimeEquals(prior.Hash, fingerprint)) Reject(session);
            return new(CheckoutOutcome.Replayed, request.OrderId);
        }
        var product = await session.RunEfStepAsync(false, async (data, token) =>
        {
            await data.LockProductAsync(request.ProductId, token);
            return await data.Data.Set<Entities.Product>().Where(value => value.Id == request.ProductId).Select(value => new Product(value.Active, value.FulfillmentKind, value.AvailabilityPolicy, value.LocalFirst, value.Revision, value.Title, value.Description, value.IconRef, value.Guidance)).SingleOrDefaultAsync(token);
        }, ct);
        if (product is null || !product.Active || product.Kind != "local" || product.Availability != "immediate") return new(CheckoutOutcome.Unavailable);
        var brand = await session.RunEfStepAsync(false, async (data, token) =>
        {
            await data.LockBrandUpdateAsync(context.BrandId, token);
            return await data.Data.Set<Entities.Brand>().Where(value => value.Id == context.BrandId && value.BotId == context.BotId && value.Active).Select(value => new { Name = value.PublicName, Revision = value.PolicyVersion, Wallet = value.AllowWallet, Channels = value.RequireChannelJoin }).SingleAsync(token);
        }, ct);
        if (!brand.Wallet || brand.Channels) return new(CheckoutOutcome.Unavailable);
        var listing = await session.RunEfStepAsync(false, async (data, token) =>
        {
            await data.LockListingAsync(context.BrandId, request.ProductId, token);
            return await data.Data.Set<Entities.BrandProduct>().Where(value => value.BrandId == context.BrandId && value.ProductId == request.ProductId).Select(value => new Listing(value.Visible, value.AllowWallet, value.PriceUsdt, value.MinQuantity, value.MaxQuantity, value.Revision,
                value.TitleMode == "override" ? value.TitleOverride! : product.Title, value.DescriptionMode == "override" ? value.DescriptionOverride : value.DescriptionMode == "clear" ? null : product.Description,
                value.IconMode == "override" ? value.IconOverride : value.IconMode == "clear" ? null : product.Icon, value.GuidanceMode == "override" ? value.GuidanceOverride : value.GuidanceMode == "clear" ? null : product.Guidance)).SingleOrDefaultAsync(token);
        }, ct);
        if (listing is null || !listing.Visible || !listing.Wallet || request.Quantity < listing.Min || request.Quantity > listing.Max) return new(CheckoutOutcome.Unavailable);
        if (listing.Price != request.UnitPrice || listing.Revision != request.ListingRevision || product.Revision != request.ProductRevision || brand.Revision != request.PolicyRevision) return new(CheckoutOutcome.QuoteChanged);
        bool guarded = await session.RunEfStepAsync(false, async (data, token) =>
            await data.Data.Set<Entities.Order>().AnyAsync(value => value.UserId == member.Id && (value.PaymentStatus == "awaiting_payment" || value.PaymentStatus == "under_review" || value.PaymentStatus == "confirmation_pending"), token) ||
            await data.Data.Set<Entities.Order>().AnyAsync(value => value.ProductId == request.ProductId && value.IsPreorder && (value.PaymentStatus == "paid" || value.PaymentStatus == "not_required") && (value.FulfillmentStatus == "waiting_stock" || value.FulfillmentStatus == "requires_admin"), token) ||
            await data.Data.Set<Entities.Discount>().AnyAsync(value => value.BrandId == context.BrandId && value.Active && (value.ProductId == null || value.ProductId == request.ProductId) && (value.EligibleUserId == null || value.EligibleUserId == member.Id) && (value.StartsAt == null || value.StartsAt <= EfValues.UtcNow()) && (value.EndsAt == null || value.EndsAt > EfValues.UtcNow()), token), ct);
        if (guarded) return new(CheckoutOutcome.Unavailable);
        Money total = new Money(listing.Price, Currency.Usd).MultiplyQuantity(request.Quantity);
        var wallet = await WalletOperations.ReadAsync(session, member.Id, ct);
        if (wallet.Balance.Amount < total.Amount) return new(CheckoutOutcome.InsufficientBalance);
        var stock = await ReadStockAsync(session, request.ProductId, request.Quantity, ct);
        if (stock.Count != request.Quantity) return new(CheckoutOutcome.OutOfStock);
        await session.RunEfStepAsync(true, async (data, token) =>
        {
            DateTime now = await data.BrandClockAsync(context.BrandId, token);
            await CheckoutQueries.InsertAsync(data, context, verifiedActorId, new Entities.Order { Id = request.OrderId, UserId = member.Id, ProductId = request.ProductId, Quantity = request.Quantity, OrderKind = "product_purchase", PaymentStatus = "paid", Currency = "USDT", PaymentMethod = "wallet", UnitPriceUsdt = listing.Price, AutoPriceTomanSnapshot = false, PricingRoundingStepSnapshot = 5000m, PriceCalculationVersion = "local-wallet-v1", BrandPublicNameSnapshot = brand.Name, ProductTitleSnapshot = listing.Title, ProductDescriptionSnapshot = listing.Description, ProductIconSnapshot = listing.Icon, ProductGuidanceSnapshot = listing.Guidance, PaymentTermsSnapshot = "USD store credit; complete-order delivery", FulfillmentKindSnapshot = "local", LocalFirstSnapshot = product.LocalFirst, IsPreorder = false, PaymentConfirmedAt = now }, fingerprint, token);
            return true;
        }, ct);
        await BotActivityWriter.CheckoutAsync(session, context, verifiedActorId, request.OrderId, ct);
        await WalletOperations.PostAsync(session, member.Id, new Money(-total.Amount, Currency.Usd), wallet.Version,
            new Entities.WalletEntry { Id = Guid.CreateVersion7(), MovementKind = "purchase_debit", IdempotencyKey = "checkout:" + request.OrderId.ToString("D"), OrderId = request.OrderId, Reason = "customer_request" }, ct);
        await AcquireAsync(session, context, request.OrderId, stock, ct);
        await OutboxStore.EnqueueAsync(session, JobIntent.Order(JobKind.PrepareManifest, context.BrandId, request.OrderId), request.OrderId, DateTime.UtcNow, ct);
        return new(CheckoutOutcome.Purchased, request.OrderId);
    }

    internal static byte[] Fingerprint(BrandContext context, Guid member, LocalCheckoutRequest request)
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("local-wallet-v1"); writer.Write(context.BrandId.ToByteArray()); writer.Write(member.ToByteArray()); writer.Write(request.ProductId.ToByteArray());
            writer.Write(request.Quantity); writer.Write(ExactDecimal.Format(request.UnitPrice)); writer.Write(request.ListingRevision); writer.Write(request.ProductRevision); writer.Write(request.PolicyRevision);
        }
        return SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)));
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Reject(DbSession session) { session.RejectApplication(); throw new UnauthorizedAccessException("Checkout request is unavailable."); }
    private sealed record Product(bool Active, string Kind, string Availability, bool LocalFirst, long Revision, string Title, string? Description, string? Icon, string? Guidance);
    private sealed record Listing(bool Visible, bool Wallet, decimal Price, int Min, int Max, long Revision, string Title, string? Description, string? Icon, string? Guidance);
    internal sealed record Stock(Guid Id, byte[] Content, string Key);
}
