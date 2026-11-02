using Platform.Core;
using Platform.Persistence;
using Platform.Security;

namespace Platform.Telegram;

public sealed class WalletCheckoutService(DbSessionFactory sessions, CheckoutStore checkout, CatalogStore catalog)
{
    public async Task<CheckoutResult> PurchaseAsync(BrandContext context, long verifiedActorId, CheckoutRequest request,
        Func<long, long, CancellationToken, Task<ChannelMembership>> checkChannels, CancellationToken ct = default)
    {
        CheckoutResult? prior = await checkout.ReplayAsync(context, verifiedActorId, request, ct);
        if (prior is not null) return prior;
        BrandPolicySnapshot policy;
        await using (var read = await sessions.BeginAsync(ct)) policy = await BrandPolicyStore.ReadAsync(read, context, verifiedActorId, ct);
        ChannelAccess? access = await BrandPolicy.CheckChannelsAsync(policy, verifiedActorId, checkChannels, ct);
        if (access is null)
        { await RecordRejectionAsync(context, verifiedActorId, request, ct); return new(CheckoutOutcome.Unavailable); }
        try
        {
            PreparedCheckout prepared = await catalog.PrepareAsync(context, verifiedActorId, new(request.ProductId, request.Quantity, request.Payment, request.Kind), ct);
            return await AcceptAsync(context, verifiedActorId, request, prepared, access, ct);
        }
        catch (Exception error) when (error is PricingRejectedException or DiscountQuantityException or UnauthorizedAccessException)
        {
            CheckoutResult? committed = await checkout.ReplayAsync(context, verifiedActorId, request, ct);
            if (committed is not null) return committed;
            if (error is PricingRejectedException or DiscountQuantityException) await RecordRejectionAsync(context, verifiedActorId, request, ct);
            throw;
        }
    }

    public async Task<CheckoutResult> AcceptAsync(BrandContext context, long verifiedActorId, CheckoutRequest request,
        PreparedCheckout prepared, ChannelAccess access, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            CheckoutResult? prior = await checkout.ReplayLockedAsync(session, context, verifiedActorId, request, ct);
            if (prior is not null) return prior;
            await catalog.RequireCurrentAsync(session, context, verifiedActorId, prepared, ct);
            await access.RequireCurrentAsync(session, context, verifiedActorId, ct);
            CheckoutResult result = await checkout.AcceptPreparedAsync(session, context, verifiedActorId, request, prepared, catalog, ct);
            if (result.Outcome is not (CheckoutOutcome.Purchased or CheckoutOutcome.AwaitingPayment or CheckoutOutcome.Replayed))
                await RecordRejectionAsync(session, context, verifiedActorId, request, ct);
            await session.CommitAsync(ct);
            return result;
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
    private async Task RecordRejectionAsync(BrandContext context, long actor, CheckoutRequest request, CancellationToken ct)
    {
        await using var session = await sessions.BeginAsync(ct);
        await RecordRejectionAsync(session, context, actor, request, ct); await session.CommitAsync(ct);
    }
    private static async Task RecordRejectionAsync(DbSession session, BrandContext context, long actor, CheckoutRequest request, CancellationToken ct)
    {
        await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM (SELECT id FROM shop.users WHERE brand_id=$1 AND telegram_id=$2 FOR UPDATE) u", DbValue.Uuid(context.BrandId), DbValue.Bigint(actor)), r => r.GetInt64(0), ct);
        BrandUser member = await BrandUserStore.RequireAsync(session, context, actor, null, ct);
        if (member.Blocked || member.Banned) return;
        var origin = await session.QuerySingleAsync(new DbStatement("""
            SELECT EXISTS(SELECT 1 FROM shop.brand_products l JOIN shop.products p ON p.id=l.product_id
                WHERE l.brand_id=$1 AND l.product_id=$2 AND l.visible AND p.active),clock_timestamp()
            """, DbValue.Uuid(context.BrandId), DbValue.Uuid(request.ProductId)), r => (Allowed: r.GetBoolean(0), Now: r.GetUtc(1)), ct);
        if (origin.Allowed) await BotActivityWriter.RecordAsync(session, context, actor, member.Id, BotActivityAction.CheckoutRejected, BotActivitySource.Flow, BotActivityOutcome.Rejected,
            new(request.ProductId, Quantity: request.Quantity), Guid.NewGuid(), origin.Now, ct);
    }
}
