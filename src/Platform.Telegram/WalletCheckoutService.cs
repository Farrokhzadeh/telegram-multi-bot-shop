using Platform.Core;
using Platform.Persistence;
using Platform.Security;

namespace Platform.Telegram;

public sealed class WalletCheckoutService(DbSessionFactory sessions, CheckoutStore checkout, CatalogStore catalog)
{
    public Task<CheckoutResult> PurchaseAsync(BrandContext context, long verifiedActorId, CheckoutRequest request,
        Func<long, long, CancellationToken, Task<ChannelMembership>> checkChannels, CancellationToken ct = default) => PurchaseCoreAsync(context, verifiedActorId, request, checkChannels, null, ct);
    public Task<CheckoutResult> PurchaseForBrandAsync(BrandContext context, long expectedRevision, long verifiedActorId, CheckoutRequest request,
        Func<long, long, CancellationToken, Task<ChannelMembership>> checkChannels, CancellationToken ct = default) => PurchaseCoreAsync(context, verifiedActorId, request, checkChannels, expectedRevision, ct);
    private async Task<CheckoutResult> PurchaseCoreAsync(BrandContext context, long verifiedActorId, CheckoutRequest request,
        Func<long, long, CancellationToken, Task<ChannelMembership>> checkChannels, long? writerRevision, CancellationToken ct)
    {
        writerRevision ??= sessions.ExpectedWriterRevision(context);
        if (writerRevision is < 0) throw new ArgumentOutOfRangeException(nameof(writerRevision));
        CheckoutResult? prior = await ReplayAsync(context, verifiedActorId, request, writerRevision, ct);
        if (prior is not null) return prior;
        BrandPolicySnapshot policy;
        await using (var read = await sessions.BeginAsync(ct)) policy = await BrandPolicyStore.ReadAsync(read, context, verifiedActorId, ct);
        if (writerRevision is long policyRevision && policy.Revision != policyRevision) throw new UnauthorizedAccessException();
        ChannelAccess? access = await BrandPolicy.CheckChannelsAsync(policy, verifiedActorId, checkChannels, ct);
        await using (var observation = await sessions.BeginAsync(ct))
        {
            BrandUser member = await BrandUserStore.RequireAsync(observation, context, verifiedActorId, null, ct);
            await AdmitAsync(observation, context, verifiedActorId, null, writerRevision, ct);
            DateTime at = await observation.QuerySingleAsync(new DbStatement("SELECT clock_timestamp()"), r => r.GetUtc(0), ct);
            await BotActivityWriter.RecordAsync(observation, context, verifiedActorId, member.Id, BotActivityAction.PolicyCheck, BotActivitySource.System,
                access is null ? BotActivityOutcome.Blocked : BotActivityOutcome.Accepted, new(), Guid.NewGuid(), at, ct);
            await observation.CommitAsync(ct);
        }
        if (access is null)
        { await RecordRejectionAsync(context, verifiedActorId, request, writerRevision, ct); return new(CheckoutOutcome.Unavailable); }
        try
        {
            CatalogQuery query = new(request.ProductId, request.Quantity, request.Payment, request.Kind);
            PreparedCheckout prepared = writerRevision is long revision
                ? await catalog.PrepareForBrandAsync(context, revision, verifiedActorId, query, ct)
                : await catalog.PrepareAsync(context, verifiedActorId, query, ct);
            return await AcceptCoreAsync(context, verifiedActorId, request, prepared, access, writerRevision, ct);
        }
        catch (Exception error) when (error is PricingRejectedException or DiscountQuantityException or UnauthorizedAccessException)
        {
            CheckoutResult? committed = await ReplayAsync(context, verifiedActorId, request, writerRevision, ct);
            if (committed is not null) return committed;
            if (error is PricingRejectedException or DiscountQuantityException) await RecordRejectionAsync(context, verifiedActorId, request, writerRevision, ct);
            throw;
        }
    }

    public Task<CheckoutResult> AcceptAsync(BrandContext context, long verifiedActorId, CheckoutRequest request,
        PreparedCheckout prepared, ChannelAccess access, CancellationToken ct = default) => AcceptCoreAsync(context, verifiedActorId, request, prepared, access, null, ct);
    public Task<CheckoutResult> AcceptForBrandAsync(BrandContext context, long expectedRevision, long verifiedActorId, CheckoutRequest request,
        PreparedCheckout prepared, ChannelAccess access, CancellationToken ct = default) => AcceptCoreAsync(context, verifiedActorId, request, prepared, access, expectedRevision, ct);
    private async Task<CheckoutResult> AcceptCoreAsync(BrandContext context, long verifiedActorId, CheckoutRequest request,
        PreparedCheckout prepared, ChannelAccess access, long? writerRevision, CancellationToken ct)
    {
        writerRevision ??= sessions.ExpectedWriterRevision(context);
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            if (writerRevision is not null) _ = await AdminBindingStore.IsVerifiedStaffAsync(session, verifiedActorId, ct);
            CheckoutResult? prior = await checkout.ReplayLockedAsync(session, context, verifiedActorId, request, ct, writerRevision);
            if (prior is not null) return prior;
            if (writerRevision is long revision) await catalog.RequireCurrentForBrandAsync(session, context, revision, verifiedActorId, prepared, ct);
            else await catalog.RequireCurrentAsync(session, context, verifiedActorId, prepared, ct);
            await access.RequireCurrentAsync(session, context, verifiedActorId, ct);
            CheckoutResult result = await checkout.AcceptPreparedAsync(session, context, verifiedActorId, request, prepared, catalog, ct);
            if (result.Outcome is not (CheckoutOutcome.Purchased or CheckoutOutcome.AwaitingPayment or CheckoutOutcome.Replayed))
                await BotActivityWriter.CheckoutRejectedAsync(session, context, verifiedActorId, request.ProductId, request.Quantity, ct);
            await session.CommitAsync(ct);
            return result;
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
    private async Task RecordRejectionAsync(BrandContext context, long actor, CheckoutRequest request, long? writerRevision, CancellationToken ct)
    {
        await using var session = await sessions.BeginAsync(ct);
        await AdmitAsync(session, context, actor, request.ProductId, writerRevision, ct);
        await BotActivityWriter.CheckoutRejectedAsync(session, context, actor, request.ProductId, request.Quantity, ct); await session.CommitAsync(ct);
    }
    private Task<CheckoutResult?> ReplayAsync(BrandContext context, long actor, CheckoutRequest request, long? writerRevision, CancellationToken ct) =>
        writerRevision is long revision ? checkout.ReplayForBrandAsync(context, revision, actor, request, ct) : checkout.ReplayAsync(context, actor, request, ct);
    private static async Task AdmitAsync(DbSession session, BrandContext context, long actor, Guid? product, long? writerRevision, CancellationToken ct)
    {
        if (writerRevision is not long revision) return;
        await BrandWriterStore.RequireCustomerAsync(session, context, actor, revision, product, ct);
    }
}
