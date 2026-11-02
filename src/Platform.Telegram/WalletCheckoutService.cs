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
                await BotActivityWriter.CheckoutRejectedAsync(session, context, verifiedActorId, request.ProductId, request.Quantity, ct);
            await session.CommitAsync(ct);
            return result;
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
    private async Task RecordRejectionAsync(BrandContext context, long actor, CheckoutRequest request, CancellationToken ct)
    {
        await using var session = await sessions.BeginAsync(ct);
        await BotActivityWriter.CheckoutRejectedAsync(session, context, actor, request.ProductId, request.Quantity, ct); await session.CommitAsync(ct);
    }
}
