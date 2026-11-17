using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

public sealed record HostedInvoiceIntent(Guid Payment, string Origin, decimal UsdDue, CryptoAsset Network, string? Reference, Uri? CheckoutUrl)
{
    public override string ToString() => "HostedInvoiceIntent [redacted]";
}

public sealed partial class PaymentEvidenceStore
{
    public async Task<HostedInvoiceIntent> ReserveHostedInvoiceAsync(BrandContext context, long actor, string origin, long? writerRevision = null, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginAsync(ct);
        await LockMemberAsync(session, context, actor, ct);
        BrandUser member = await BrandUserStore.RequireAsync(session, context, actor, null, ct);
        await AdmitAsync(session, context, writerRevision, ct);
        Payment before = await LoadAsync(session, context, null, origin, false, ct);
        if (before.Member != member.Id || member.Blocked || member.Banned) Reject(session);
        if (before.Order is Guid order) await session.ExecuteAsync(new DbStatement("SELECT id FROM shop.orders WHERE id=$1 FOR UPDATE", DbValue.Uuid(order)), ct);
        Payment data = await LoadAsync(session, context, before.Id, null, true, ct);
        await RequirePayableAsync(session, data, ct);
        if (data.HostedStarted && data.HostedUrl is null) throw new InvalidOperationException("Previous invoice creation requires reconciliation.");
        if (!data.HostedStarted)
            await session.ExecuteAsync(new DbStatement("UPDATE shop.payments SET provider_code='nowpayments',hosted_invoice_started_at=clock_timestamp() WHERE id=$1", DbValue.Uuid(data.Id)), ct);
        await session.CommitAsync(ct);
        return new(data.Id, data.Origin, data.Due, data.Asset == "USDTBSC" ? CryptoAsset.UsdtBsc : CryptoAsset.UsdtTrc20,
            data.HostedReference, data.HostedUrl is null ? null : new Uri(data.HostedUrl));
    }

    public async Task<bool> CompleteHostedInvoiceAsync(BrandContext context, long actor, Guid payment, string reference, Uri checkoutUrl,
        long? writerRevision = null, CancellationToken ct = default)
    {
        if (reference.Length is < 1 or > 64 || !reference.All(char.IsAsciiDigit) || !checkoutUrl.IsAbsoluteUri ||
            checkoutUrl.Scheme != Uri.UriSchemeHttps || !checkoutUrl.IsDefaultPort || checkoutUrl.IdnHost != "nowpayments.io" || checkoutUrl.UserInfo.Length != 0)
            throw new PaymentNoticeRejectedException();
        await using var session = await sessions.BeginAsync(ct);
        await LockMemberAsync(session, context, actor, ct);
        BrandUser member = await BrandUserStore.RequireAsync(session, context, actor, null, ct);
        await AdmitAsync(session, context, writerRevision, ct);
        Payment before = await LoadAsync(session, context, payment, null, false, ct);
        if (before.Member != member.Id) Reject(session);
        if (before.Order is Guid order) await session.ExecuteAsync(new DbStatement("SELECT id FROM shop.orders WHERE id=$1 FOR UPDATE", DbValue.Uuid(order)), ct);
        Payment data = await LoadAsync(session, context, payment, null, true, ct);
        if (!data.HostedStarted || data.ProviderCode != "nowpayments") Reject(session);
        if (data.HostedReference is null)
            await session.ExecuteAsync(new DbStatement("UPDATE shop.payments SET hosted_invoice_ref=$1,hosted_invoice_url=$2 WHERE id=$3",
                DbValue.Text(reference), DbValue.Text(checkoutUrl.AbsoluteUri), DbValue.Uuid(payment)), ct);
        else if (data.HostedReference != reference || data.HostedUrl != checkoutUrl.AbsoluteUri) Reject(session);
        bool payable = !member.Blocked && !member.Banned && await IsPayableAsync(session, data, ct);
        await session.CommitAsync(ct);
        return payable;
    }

    private static async Task RequirePayableAsync(DbSession session, Payment data, CancellationToken ct)
    {
        if (!await IsPayableAsync(session, data, ct)) Reject(session);
    }

    private static async Task<bool> IsPayableAsync(DbSession session, Payment data, CancellationToken ct)
    {
        if (data.Method != "crypto" || data.Currency != "USD" || data.Provider is not null ||
            data.Status is not ("created" or "awaiting") || data.Asset is not ("USDTTRC20" or "USDTBSC")) return false;
        if (data.Order is null) return true;
        return data.OrderStatus == "awaiting_payment" && data.Deadline >=
            await session.QuerySingleAsync(new DbStatement("SELECT clock_timestamp()"), r => r.GetUtc(0), ct);
    }
}
