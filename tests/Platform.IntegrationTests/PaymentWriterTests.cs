using System.Net;
using System.Text;
using Platform.Core;
using Platform.Payments;
using Platform.Persistence;
using Platform.Testing;

namespace Platform.IntegrationTests;

[Collection("PostgreSQL races")]
public sealed class PaymentWriterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static async Task<JobLease> Claim(CheckoutFixture f, int user = 0, long revision = 1)
    {
        await using var session = await f.Sessions.BeginAsync(Ct);
        var lease = Assert.IsType<JobLease>(await OutboxStore.ClaimForBrandAsync(session, f.Context(user), revision, JobKind.VerifyPayment, Guid.NewGuid(), TimeSpan.FromSeconds(60), Ct));
        await session.CommitAsync(Ct);
        return lease;
    }
    private static async Task Unsettled(CheckoutFixture f)
    {
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.payments WHERE status='confirming'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='verify_payment' AND status='leased' AND attempts=1", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PauseAfterClaimRejectsRenewalAndOldRevisionAfterResume(bool resume)
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, ct: Ct);
        var fixture = new PaymentSettlementFixture(f);
        await fixture.EnableAsync(Ct);
        var order = await fixture.OrderAsync(ct: Ct);
        await fixture.CaptureAsync(order.Payment, order.Origin, order.Due, ct: Ct);
        var lease = await Claim(f);
        var admin = await f.AdminAsync(Ct);
        var writer = new BrandWriterStore(f.Sessions);
        await writer.SetActiveAsync(admin, f.Context(0), 1, false, Guid.NewGuid(), Ct);
        if (resume) await writer.SetActiveAsync(admin, f.Context(0), 2, true, Guid.NewGuid(), Ct);
        using var provider = PaymentSettlementFixture.Client(_ => throw new InvalidOperationException("Paused worker reached transport."));
        var worker = new PaymentVerificationWorker(f.Sessions, fixture.Store, provider);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => worker.RunForBrandAsync(f.Context(0), 1, lease, Ct));
        await using (var read = await f.Sessions.BeginAsync(Ct))
            Assert.Equal(lease.Until, await read.QuerySingleAsync(new DbStatement("SELECT lease_until FROM shop.jobs WHERE id=$1", DbValue.Uuid(lease.Id)), r => r.GetUtc(0), Ct));
        await Unsettled(f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PauseDuringLookupPreventsSettlementAndFreshResumeCanReplaySafely(bool resumeDuringLookup)
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, ct: Ct);
        var fixture = new PaymentSettlementFixture(f);
        await fixture.EnableAsync(Ct);
        var order = await fixture.OrderAsync(ct: Ct);
        await fixture.CaptureAsync(order.Payment, order.Origin, order.Due, ct: Ct);
        var lease = await Claim(f);
        var admin = await f.AdminAsync(Ct);
        var writer = new BrandWriterStore(f.Sessions);
        using var provider = new NowPaymentsClient("public-synthetic-api-key", new Transport(async () =>
        {
            await writer.SetActiveAsync(admin, f.Context(0), 1, false, Guid.NewGuid(), Ct);
            if (resumeDuringLookup) await writer.SetActiveAsync(admin, f.Context(0), 2, true, Guid.NewGuid(), Ct);
        }, PaymentNoticeFixture.Body(order.Origin, quote: order.Due, provider: PaymentSettlementFixture.Provider(order.Payment))));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new PaymentVerificationWorker(f.Sessions, fixture.Store, provider).RunForBrandAsync(f.Context(0), 1, lease, Ct));
        await Unsettled(f);
        if (!resumeDuringLookup) await writer.SetActiveAsync(admin, f.Context(0), 2, true, Guid.NewGuid(), Ct);
        using var resumed = PaymentSettlementFixture.Client(_ => PaymentNoticeFixture.Body(order.Origin, quote: order.Due, provider: PaymentSettlementFixture.Provider(order.Payment)));
        Assert.Equal(PaymentSettlementOutcome.Settled, (await new PaymentVerificationWorker(f.Sessions, fixture.Store, resumed).RunForBrandAsync(f.Context(0), 3, lease, Ct))!.Outcome);
        Assert.Equal(PaymentSettlementOutcome.Replayed, (await fixture.Store.SettleForBrandAsync(f.Context(0), 3, order.Payment, (_, _) => throw new InvalidOperationException(), Ct)).Outcome);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments WHERE status='acquired'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='verify_payment' AND status='succeeded'", Ct));
    }

    [Theory]
    [InlineData("finished", PaymentSettlementOutcome.Settled, "succeeded")]
    [InlineData("waiting", PaymentSettlementOutcome.Pending, "retry")]
    public async Task ActiveWorkerFinishesOrRetriesWithoutChangingOtherBrands(string status, PaymentSettlementOutcome outcome, string jobStatus)
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, ct: Ct);
        var fixture = new PaymentSettlementFixture(f);
        await fixture.EnableAsync(Ct);
        var order = await fixture.OrderAsync(ct: Ct);
        await fixture.CaptureAsync(order.Payment, order.Origin, order.Due, ct: Ct);
        var lease = await Claim(f);
        using var provider = PaymentSettlementFixture.Client(_ => PaymentNoticeFixture.Body(order.Origin, status, quote: order.Due, provider: PaymentSettlementFixture.Provider(order.Payment)));
        var worker = new PaymentVerificationWorker(f.Sessions, fixture.Store, provider);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => worker.RunForBrandAsync(f.Context(1), 1, lease, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => worker.RunForBrandAsync(f.Context(0), -1, lease, Ct));
        Assert.Equal(outcome, (await worker.RunForBrandAsync(f.Context(0), 1, lease, Ct))!.Outcome);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='verify_payment' AND status='" + jobStatus + "' AND locked_by IS NULL AND lease_until IS NULL", Ct));
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.brands WHERE active AND policy_version=1 AND id NOT IN (SELECT brand_id FROM shop.users WHERE id IN (SELECT user_id FROM shop.orders))", Ct));
    }

    [Fact]
    public async Task DirectSettlementRejectsPausedAndForeignBrandBeforeTransport()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, ct: Ct);
        var fixture = new PaymentSettlementFixture(f);
        await fixture.EnableAsync(Ct);
        var order = await fixture.OrderAsync(ct: Ct);
        await fixture.CaptureAsync(order.Payment, order.Origin, order.Due, ct: Ct);
        await new BrandWriterStore(f.Sessions).SetActiveAsync(await f.AdminAsync(Ct), f.Context(0), 1, false, Guid.NewGuid(), Ct);
        Task<ProviderPaymentSnapshot> Reject(InvoiceExpectation _, CancellationToken token) => throw new InvalidOperationException("Rejected settlement reached transport.");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Store.SettleForBrandAsync(f.Context(0), 2, order.Payment, Reject, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Store.SettleForBrandAsync(f.Context(1), 1, order.Payment, Reject, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Store.SettleForBrandAsync(f.Context(0), -1, order.Payment, Reject, Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments", Ct));
    }

    [Fact]
    public async Task AnotherBrandSettlesWhileClaimedBrandIsPaused()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, stock: 2, ct: Ct);
        var fixture = new PaymentSettlementFixture(f);
        await fixture.EnableAsync(Ct);
        var first = await fixture.OrderAsync(ct: Ct);
        await fixture.CaptureAsync(first.Payment, first.Origin, first.Due, ct: Ct);
        await Claim(f);
        await new BrandWriterStore(f.Sessions).SetActiveAsync(await f.AdminAsync(Ct), f.Context(0), 1, false, Guid.NewGuid(), Ct);
        var second = await fixture.OrderAsync(user: 1, ct: Ct);
        await fixture.CaptureAsync(second.Payment, second.Origin, second.Due, user: 1, ct: Ct);
        var lease = await Claim(f, 1);
        using var provider = PaymentSettlementFixture.Client(_ => PaymentNoticeFixture.Body(second.Origin, quote: second.Due, provider: PaymentSettlementFixture.Provider(second.Payment)));
        Assert.Equal(PaymentSettlementOutcome.Settled, (await new PaymentVerificationWorker(f.Sessions, fixture.Store, provider).RunForBrandAsync(f.Context(1), 1, lease, Ct))!.Outcome);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='verify_payment' AND status='leased'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.payments WHERE status='confirming'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.payments WHERE status='settled'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments WHERE status='acquired'", Ct));
    }

    private sealed class Transport(Func<Task> beforeResponse, string body) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (request.Method != HttpMethod.Get || request.RequestUri?.Host != "api.nowpayments.io") throw new InvalidOperationException();
            await beforeResponse();
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
