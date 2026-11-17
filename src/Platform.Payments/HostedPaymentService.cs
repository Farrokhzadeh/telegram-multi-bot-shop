using Platform.Core;
using Platform.Persistence;
using Platform.Security;

namespace Platform.Payments;

public sealed class HostedPaymentService(PaymentEvidenceStore store, NowPaymentsClient provider, Uri callback)
{
    public Task<HostedPaymentInvoice> IssueForOrderAsync(BrandContext context, long actor, Guid order, long? writerRevision = null, CancellationToken ct = default) =>
        IssueAsync(context, actor, "order:" + order.ToString("D"), writerRevision, ct);

    public Task<HostedPaymentInvoice> IssueForTopupAsync(BrandContext context, long actor, Guid payment, long? writerRevision = null, CancellationToken ct = default) =>
        IssueAsync(context, actor, "topup:" + payment.ToString("D"), writerRevision, ct);

    private async Task<HostedPaymentInvoice> IssueAsync(BrandContext context, long actor, string origin, long? writerRevision, CancellationToken ct)
    {
        var intent = await store.ReserveHostedInvoiceAsync(context, actor, origin, writerRevision, ct);
        if (intent.CheckoutUrl is Uri checkout) return new(intent.Reference!, checkout);
        var issued = await provider.CreateHostedInvoiceAsync(intent.Origin, intent.UsdDue, intent.Network, callback, ct);
        if (!await store.CompleteHostedInvoiceAsync(context, actor, intent.Payment, issued.ProviderInvoice, issued.CheckoutUrl, writerRevision, ct))
            throw new PaymentNoticeRejectedException();
        return issued;
    }
}
