using Platform.Core;
using Platform.Payments;
using Platform.Persistence;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class PaymentExpiryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task CanonicalPaymentDeadlineAndMembershipGuardsNeedNoDuplicateMigration() => await PaymentDeadlineQualification.SchemaAsync(Ct);
    [Fact]
    public async Task ExpiryReplaysOnceReleasesWholeClaimAndKeepsOriginalInvoiceAndQuote() => await PaymentDeadlineQualification.ExpiryAsync(Ct);
    [Fact]
    public async Task ExistingEvidenceAndReviewObligationsCannotExpireOrDonateTheirClaims() => await PaymentDeadlineQualification.ProtectionAsync(Ct);
    [Fact]
    public async Task StatusEditsRecheckCurrentPaymentAndNeverExpireOrOverwritePaidOrders() => await PaymentMessageQualification.ProjectionAsync(Ct);
    [Fact]
    public async Task RateLimitsAndAuditedFallbackRetainSavedTargetsAndKnownLateReceipts() => await PaymentMessageQualification.RecoveryAsync(Ct);
    [Fact]
    public async Task UnknownNewMessagePausesUntilLateKnownSuccessWithoutAnAutomaticResend() => await PaymentMessageQualification.TimeoutAsync(Ct);
    [Fact]
    public async Task ReviewObligationCanCoexistWithOneIndependentPendingOrder()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        var payments = new PaymentSettlementFixture(f); await payments.EnableAsync(Ct);
        var old = await payments.OrderAsync(ct: Ct);
        await payments.CaptureAsync(old.Payment, old.Origin, old.Due, ct: Ct);
        var next = await payments.OrderAsync(ct: Ct);
        Assert.NotEqual(old.Order, next.Order);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='confirmation_pending' AND resolution_kind IS NULL", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='awaiting_payment'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", Ct));
    }
}
