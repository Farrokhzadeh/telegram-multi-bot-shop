using System.Security.Cryptography;
using Platform.Core;
using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

[Collection("PostgreSQL races")]
public sealed class PaymentOriginWriterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static PaymentEvidenceStore Store(CheckoutFixture f) => new(f.Sessions, f.Secrets);
    private static Task<Guid> Create(CheckoutFixture f, Guid payment, long revision = 1, int user = 0)
        => Store(f).CreateTopupForBrandAsync(f.Context(user), revision, CheckoutFixture.Actor(user), payment, new(10.25m, Currency.Usd), CryptoAsset.UsdtBsc, Ct);
    private static async Task<Guid> Bound(CheckoutFixture f)
    {
        Guid payment = await Create(f, Guid.NewGuid());
        await using var bind = await f.Sessions.BeginAsync(Ct);
        await Store(f).BindInvoiceForBrandAsync(bind, f.Context(0), 1, CheckoutFixture.Actor(0), payment, PaymentSettlementFixture.Provider(payment), 10.4m, Ct);
        await bind.CommitAsync(Ct);
        return payment;
    }
    private static VerifiedPaymentNotice Notice(Guid payment) => PaymentNoticeFixture.Notice("topup:" + payment.ToString("D"), asset: "usdtbsc", provider: PaymentSettlementFixture.Provider(payment));
    private static Task<string> Archive(ProtectedPaymentEvidence evidence, CancellationToken ct) => Task.FromResult("protected:synthetic/" + evidence.Digest);
    private static async Task Uncaptured(CheckoutFixture f)
    {
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.payments WHERE ipn_verified_at IS NOT NULL OR first_payment_observed_at IS NOT NULL OR confirmed_at IS NOT NULL", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='verify_payment'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", Ct));
    }

    [Fact]
    public async Task TopupAndInvoiceReplayNeedFreshAdmissionAfterResume()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, ct: Ct);
        await new PaymentSettlementFixture(f).EnableAsync(Ct);
        Guid payment = await Create(f, Guid.NewGuid());
        Assert.Equal(payment, await Create(f, payment));
        var writer = new BrandWriterStore(f.Sessions);
        var admin = await f.AdminAsync(Ct);
        await writer.SetActiveAsync(admin, f.Context(0), 1, false, Guid.NewGuid(), Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Create(f, Guid.NewGuid(), 2));
        await writer.SetActiveAsync(admin, f.Context(0), 2, true, Guid.NewGuid(), Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Create(f, payment));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Create(f, Guid.NewGuid()));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Create(f, Guid.NewGuid(), -1));
        Assert.Equal(payment, await Create(f, payment, 3));
        await using (var bind = await f.Sessions.BeginAsync(Ct))
        {
            for (int i = 0; i < 2; i++) await Store(f).BindInvoiceForBrandAsync(bind, f.Context(0), 3, CheckoutFixture.Actor(0), payment, PaymentSettlementFixture.Provider(payment), 10.4m, Ct);
            await bind.CommitAsync(Ct);
        }
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.payments WHERE status='awaiting' AND provider_code='nowpayments'", Ct));
        await Uncaptured(f);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(2)]
    public async Task RejectedInvoiceAdmissionPoisonsAllBorrowedTransactionWrites(long revision)
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, ct: Ct);
        await new PaymentSettlementFixture(f).EnableAsync(Ct);
        Guid payment = await Create(f, Guid.NewGuid());
        string language;
        await using (var read = await f.Sessions.BeginAsync(Ct)) language = await read.QuerySingleAsync(new DbStatement("SELECT language FROM shop.users WHERE id=$1", DbValue.Uuid(f.Users[0])), r => r.GetString(0), Ct);
        await using (var bind = await f.Sessions.BeginAsync(Ct))
        {
            await bind.ExecuteAsync(new DbStatement("UPDATE shop.users SET language=CASE WHEN language='en' THEN 'fa' ELSE 'en' END WHERE id=$1", DbValue.Uuid(f.Users[0])), Ct);
            Task Bind() => Store(f).BindInvoiceForBrandAsync(bind, f.Context(0), revision, CheckoutFixture.Actor(0), payment, PaymentSettlementFixture.Provider(payment), 10.4m, Ct);
            if (revision < 0) await Assert.ThrowsAsync<ArgumentOutOfRangeException>(Bind);
            else await Assert.ThrowsAsync<UnauthorizedAccessException>(Bind);
            Assert.Equal(DbSessionState.Aborted, bind.State);
            await Assert.ThrowsAsync<InvalidOperationException>(() => bind.CommitAsync(Ct));
        }
        await using (var read = await f.Sessions.BeginAsync(Ct)) Assert.Equal(language, await read.QuerySingleAsync(new DbStatement("SELECT language FROM shop.users WHERE id=$1", DbValue.Uuid(f.Users[0])), r => r.GetString(0), Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.payments WHERE provider_code IS NULL AND status='created'", Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PausedStaleAndForeignCaptureCannotReachArchive(bool resume)
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, ct: Ct);
        await new PaymentSettlementFixture(f).EnableAsync(Ct);
        Guid payment = await Bound(f);
        using var notice = Notice(payment);
        var writer = new BrandWriterStore(f.Sessions);
        var admin = await f.AdminAsync(Ct);
        await writer.SetActiveAsync(admin, f.Context(0), 1, false, Guid.NewGuid(), Ct);
        if (resume) await writer.SetActiveAsync(admin, f.Context(0), 2, true, Guid.NewGuid(), Ct);
        Task<string> Reject(ProtectedPaymentEvidence _, CancellationToken token) => throw new InvalidOperationException("Rejected capture reached archive.");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Store(f).CaptureForBrandAsync(f.Context(0), 1, notice, Reject, Ct));
        if (!resume) await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Store(f).CaptureForBrandAsync(f.Context(0), 2, notice, Reject, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Store(f).CaptureForBrandAsync(f.Context(1), 1, notice, Reject, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Store(f).CaptureForBrandAsync(f.Context(0), -1, notice, Reject, Ct));
        await Uncaptured(f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PauseDuringArchiveRetainsOriginalButRejectsEvidenceUntilFreshResume(bool resume)
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, ct: Ct);
        await new PaymentSettlementFixture(f).EnableAsync(Ct);
        Guid payment = await Bound(f);
        using var notice = Notice(payment);
        var writer = new BrandWriterStore(f.Sessions);
        var admin = await f.AdminAsync(Ct);
        ProtectedPaymentEvidence? retained = null;
        async Task<string> Pause(ProtectedPaymentEvidence evidence, CancellationToken ct)
        {
            retained = evidence with { Ciphertext = evidence.Ciphertext.ToArray() };
            await writer.SetActiveAsync(admin, f.Context(0), 1, false, Guid.NewGuid(), ct);
            if (resume) await writer.SetActiveAsync(admin, f.Context(0), 2, true, Guid.NewGuid(), ct);
            return await Archive(evidence, ct);
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Store(f).CaptureForBrandAsync(f.Context(0), 1, notice, Pause, Ct));
        var saved = Assert.IsType<ProtectedPaymentEvidence>(retained);
        byte[] plain = new BrandCipher(f.Secrets).Decrypt(f.Context(0), "payments", payment, "ipn_evidence", saved.EncryptionKeyId, saved.Ciphertext.Span);
        try { Assert.Equal(PaymentNoticeFixture.Body(notice.Origin, asset: notice.Asset, provider: notice.ProviderPayment), System.Text.Encoding.UTF8.GetString(plain)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        await Uncaptured(f);
        if (!resume) await writer.SetActiveAsync(admin, f.Context(0), 2, true, Guid.NewGuid(), Ct);
        Assert.Equal(PaymentCaptureOutcome.Ready, (await Store(f).CaptureForBrandAsync(f.Context(0), 3, notice, Archive, Ct)).Outcome);
        Assert.Equal(PaymentCaptureOutcome.Replayed, (await Store(f).CaptureForBrandAsync(f.Context(0), 3, notice, Archive, Ct)).Outcome);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='verify_payment' AND status='pending'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", Ct));
    }

    [Fact]
    public async Task AnotherBrandCapturesWhileFirstArchiveIsPausedWithoutHoldingItsTransaction()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, ct: Ct);
        var fixture = new PaymentSettlementFixture(f);
        await fixture.EnableAsync(Ct);
        Guid payment = await Bound(f);
        using var first = Notice(payment);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string> Hold(ProtectedPaymentEvidence evidence, CancellationToken ct)
        { entered.SetResult(); await release.Task.WaitAsync(ct); return await Archive(evidence, ct); }
        var capture = Store(f).CaptureForBrandAsync(f.Context(0), 1, first, Hold, Ct);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            await new BrandWriterStore(f.Sessions).SetActiveAsync(await f.AdminAsync(Ct), f.Context(0), 1, false, Guid.NewGuid(), Ct);
            var other = await fixture.OrderAsync(user: 1, ct: Ct);
            using var second = PaymentNoticeFixture.Notice(other.Origin, quote: other.Due, provider: PaymentSettlementFixture.Provider(other.Payment));
            Assert.Equal(PaymentCaptureOutcome.Ready, (await Store(f).CaptureForBrandAsync(f.Context(1), 1, second, Archive, Ct)).Outcome);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => capture);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='verify_payment'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.payments WHERE status='awaiting' AND ipn_verified_at IS NULL", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", Ct));
    }

    [Fact]
    public async Task HundredScopedReplaysKeepOneNoticeAndVerificationWithoutSettlement()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, ct: Ct);
        await new PaymentSettlementFixture(f).EnableAsync(Ct);
        Guid payment = await Bound(f);
        var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(async _ =>
        {
            using var notice = Notice(payment);
            return await Store(f).CaptureForBrandAsync(f.Context(0), 1, notice, Archive, Ct);
        }));
        Assert.Single(results, r => r.Outcome == PaymentCaptureOutcome.Ready);
        Assert.Equal(99, results.Count(r => r.Outcome == PaymentCaptureOutcome.Replayed));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='verify_payment'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", Ct));
    }

    [Fact]
    public async Task PauseWaitsForAdmittedInvoiceTransactionAndPreservesCommittedBinding()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, ct: Ct);
        await new PaymentSettlementFixture(f).EnableAsync(Ct);
        Guid payment = await Create(f, Guid.NewGuid());
        var admin = await f.AdminAsync(Ct);
        await using var bind = await f.Sessions.BeginAsync(Ct);
        int pid = await bind.QuerySingleAsync(new DbStatement("SELECT pg_backend_pid()"), r => r.GetInt32(0), Ct);
        await Store(f).BindInvoiceForBrandAsync(bind, f.Context(0), 1, CheckoutFixture.Actor(0), payment, PaymentSettlementFixture.Provider(payment), 10.4m, Ct);
        var pause = new BrandWriterStore(f.Sessions).SetActiveAsync(admin, f.Context(0), 1, false, Guid.NewGuid(), Ct);
        bool blocked = false;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            while (!blocked)
            {
                await using var read = await f.Sessions.BeginAsync(limit.Token);
                blocked = await read.QuerySingleAsync(new DbStatement("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND $1=ANY(pg_blocking_pids(pid)))", DbValue.Integer(pid)), r => r.GetBoolean(0), limit.Token);
                if (!blocked) await Task.Delay(10, limit.Token);
            }
            Assert.False(pause.IsCompleted);
        }
        finally { await bind.CommitAsync(Ct); }
        Assert.True(blocked);
        Assert.Equal(new(false, 2), await pause);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.payments WHERE provider_code='nowpayments' AND status='awaiting'", Ct));
        await Uncaptured(f);
    }
}
