using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Platform.Core;
using Platform.Persistence;
using Platform.Security;
using Platform.Telegram;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class ExternalPaymentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private sealed class Archive
    {
        public ConcurrentDictionary<string, (byte[] Bytes, string Key)> Items { get; } = new();
        public Task<string> Save(ProtectedPaymentEvidence evidence, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            string asset = "protected:synthetic/" + evidence.BrandId.ToString("N") + "/" + evidence.PaymentId.ToString("N") + "/" + evidence.Digest;
            Items.TryAdd(asset, (evidence.Ciphertext.ToArray(), evidence.EncryptionKeyId));
            return Task.FromResult(asset);
        }
    }
    private static PaymentEvidenceStore Store(CheckoutFixture f) => new(f.Sessions, f.Secrets);

    [Fact]
    public async Task StandaloneTopupNeedsNoOrderOrWalletAndReplaysItsFrozenOriginalIntent()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, stock: 0, ct: Ct);
        await Enable(f, false);
        await using (var start = await f.Sessions.BeginAsync(Ct))
        { await BrandUserStore.ObservePrivateAsync(start, f.Context(0), 9999, Ct); await start.CommitAsync(Ct); }
        Guid id = Guid.NewGuid();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Store(f).CreateTopupAsync(f.Context(0), 9999, id, new(10.25m, Currency.Usd), CryptoAsset.UsdtBsc, Ct));
        await Enable(f, true);
        Assert.Equal(id, await Store(f).CreateTopupAsync(f.Context(0), 9999, id, new(10.25m, Currency.Usd), CryptoAsset.UsdtBsc, Ct));
        Assert.Equal(id, await Store(f).CreateTopupAsync(f.Context(0), 9999, id, new(10.25m, Currency.Usd), CryptoAsset.UsdtBsc, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Store(f).CreateTopupAsync(f.Context(0), 9999, id, new(20, Currency.Usd), CryptoAsset.UsdtBsc, Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.payments WHERE order_id IS NULL AND topup_user_id IS NOT NULL AND amount_due=10.25 AND network_asset='USDTBSC'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders", Ct));
        Assert.Equal(3L, await f.CountAsync("SELECT count(*) FROM shop.wallets", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", Ct));
    }

    [Fact]
    public async Task InvoiceBindingIsImmutableAndForeignMembershipsCannotBindIt()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        await Enable(f);
        var origin = await Order(f);
        await using (var foreign = await f.Sessions.BeginAsync(Ct))
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Store(f).BindInvoiceAsync(foreign, f.Context(1), CheckoutFixture.Actor(1), origin.Id, "123456789", 10.4m, Ct));
        await using (var changed = await f.Sessions.BeginAsync(Ct))
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Store(f).BindInvoiceAsync(changed, f.Context(0), CheckoutFixture.Actor(0), origin.Id, "999", 1, Ct));
        await using (var direct = await f.Sessions.BeginAsync(Ct))
        {
            var error = await Assert.ThrowsAsync<DbOperationException>(() => direct.ExecuteAsync(new DbStatement("UPDATE shop.payments SET expected_network_amount=1 WHERE id=$1", DbValue.Uuid(origin.Id)), Ct));
            Assert.Equal(DbFailureKind.Constraint, error.Failure.Kind);
        }
    }

    [Fact]
    public async Task FinishedEvidenceArchivesEncryptedProofAndKeepsUsdQuoteSeparateFromTokensWithoutPosting()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        await Enable(f);
        var origin = await Order(f);
        var archive = new Archive();
        using var notice = PaymentNoticeFixture.Notice(origin.Origin);
        Assert.Equal(PaymentCaptureOutcome.Ready, (await Store(f).CaptureAsync(f.Context(0), notice, archive.Save, Ct)).Outcome);
        await using var read = await f.Sessions.BeginAsync(Ct);
        var values = await read.QuerySingleAsync(new DbStatement("SELECT amount_due::text,amount_received::text,network_amount::text,expected_network_amount::text,status,ipn_key_id,evidence_asset_ref,evidence_digest,ipn_signature FROM shop.payments WHERE id=$1", DbValue.Uuid(origin.Id)),
            r => (Due: ExactDecimal.Parse(r.GetString(0)), Received: ExactDecimal.Parse(r.GetString(1)), Tokens: ExactDecimal.Parse(r.GetString(2)), Expected: ExactDecimal.Parse(r.GetString(3)), Status: r.GetString(4), Key: r.GetString(5), Asset: r.GetString(6), Digest: r.GetBinary(7), Signature: r.GetBinary(8)), Ct);
        Assert.Equal((10.25m, 10.25m, 10.4m, 10.4m, "confirming", "synthetic-v1"), (values.Due, values.Received, values.Tokens, values.Expected, values.Status, values.Key));
        Assert.Equal(64, values.Signature.Length);
        var saved = archive.Items[values.Asset];
        byte[] plain = new BrandCipher(f.Secrets).Decrypt(f.Context(0), "payments", origin.Id, "ipn_evidence", saved.Key, saved.Bytes);
        try { Assert.Equal(values.Digest, SHA256.HashData(plain)); Assert.Equal(PaymentNoticeFixture.Body(origin.Origin), Encoding.UTF8.GetString(plain)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        Assert.Throws<CipherRejectedException>(() => new BrandCipher(f.Secrets).Decrypt(f.Context(1), "payments", origin.Id, "ipn_evidence", saved.Key, saved.Bytes));
        await NoPosting(f);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='verify_payment'", Ct));
    }

    [Fact]
    public async Task HundredConcurrentReplaysQueueOneLogicalVerificationAcrossReconstructedStores()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        await Enable(f);
        var origin = await Order(f);
        var archive = new Archive();
        var tasks = Enumerable.Range(0, 100).Select(async _ =>
        {
            using var notice = PaymentNoticeFixture.Notice(origin.Origin);
            return await Store(f).CaptureAsync(f.Context(0), notice, archive.Save, Ct);
        });
        var results = await Task.WhenAll(tasks);
        Assert.Single(results, r => r.Outcome == PaymentCaptureOutcome.Ready);
        Assert.Equal(99, results.Count(r => r.Outcome == PaymentCaptureOutcome.Replayed));
        Assert.Single(archive.Items);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='verify_payment'", Ct));
        await NoPosting(f);
    }

    [Fact]
    public async Task TimelyEvidenceKeepsItsFirstClockAndDelayedOrReorderedNoticesCannotEraseConfirmation()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        await Enable(f);
        var origin = await Order(f);
        var archive = new Archive();
        using (var pending = PaymentNoticeFixture.Notice(origin.Origin, "confirming"))
            Assert.Equal(PaymentCaptureOutcome.Pending, (await Store(f).CaptureAsync(f.Context(0), pending, archive.Save, Ct)).Outcome);
        DateTime first;
        await using (var read = await f.Sessions.BeginAsync(Ct)) first = await read.QuerySingleAsync(new DbStatement("SELECT first_payment_observed_at FROM shop.payments WHERE id=$1", DbValue.Uuid(origin.Id)), r => r.GetUtc(0), Ct);
        using (var finished = PaymentNoticeFixture.Notice(origin.Origin))
            Assert.Equal(PaymentCaptureOutcome.Ready, (await Store(f).CaptureAsync(f.Context(0), finished, archive.Save, Ct)).Outcome);
        using (var old = PaymentNoticeFixture.Notice(origin.Origin, "confirmed"))
            Assert.Equal(PaymentCaptureOutcome.Replayed, (await Store(f).CaptureAsync(f.Context(0), old, archive.Save, Ct)).Outcome);
        await using var verify = await f.Sessions.BeginAsync(Ct);
        Assert.Equal(first, await verify.QuerySingleAsync(new DbStatement("SELECT first_payment_observed_at FROM shop.payments WHERE id=$1", DbValue.Uuid(origin.Id)), r => r.GetUtc(0), Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.payments WHERE provider_status_code='finished' AND confirmed_at IS NOT NULL AND status='confirming'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='confirmation_pending'", Ct));
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='verify_payment'", Ct));
        await NoPosting(f);
    }

    [Theory]
    [InlineData("usdtbsc", "10.4", "10.25", "finished")]
    [InlineData("trx", "10.4", "10.25", "finished")]
    [InlineData("usdttrc20", "11", "10.25", "finished")]
    [InlineData("usdttrc20", "1", "10.25", "finished")]
    [InlineData("usdttrc20", "10.4", "99", "finished")]
    [InlineData("usdttrc20", "10.4", "10.25", "provider_unknown")]
    public async Task WrongNetworkValueOrStatusIsStickyReviewWithoutInventingUsdCredit(string asset, string received, string quote, string status)
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        await Enable(f);
        var origin = await Order(f);
        var archive = new Archive();
        using (var anomaly = PaymentNoticeFixture.Notice(origin.Origin, status, ExactDecimal.Parse(received), asset, ExactDecimal.Parse(quote)))
            Assert.Equal(PaymentCaptureOutcome.Review, (await Store(f).CaptureAsync(f.Context(0), anomaly, archive.Save, Ct)).Outcome);
        using (var later = PaymentNoticeFixture.Notice(origin.Origin))
            Assert.Equal(PaymentCaptureOutcome.Review, (await Store(f).CaptureAsync(f.Context(0), later, archive.Save, Ct)).Outcome);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.payments WHERE status='review' AND amount_received IS NULL AND confirmed_at IS NULL", Ct));
        await NoPosting(f);
    }

    [Fact]
    public async Task UnknownForeignOrUnboundProviderIdentityCannotArchiveOrMutateAnotherPayment()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        await Enable(f);
        var origin = await Order(f);
        var archive = new Archive();
        using (var foreign = PaymentNoticeFixture.Notice(origin.Origin))
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Store(f).CaptureAsync(f.Context(1), foreign, archive.Save, Ct));
        using (var wrong = PaymentNoticeFixture.Notice(origin.Origin, provider: "987654321"))
            await Assert.ThrowsAsync<PaymentNoticeRejectedException>(() => Store(f).CaptureAsync(f.Context(0), wrong, archive.Save, Ct));
        using (var missing = PaymentNoticeFixture.Notice("unknown-origin"))
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Store(f).CaptureAsync(f.Context(0), missing, archive.Save, Ct));
        Assert.Empty(archive.Items);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.payments WHERE status='awaiting' AND ipn_payload_sha256 IS NULL", Ct));
        await NoPosting(f);
    }

    [Fact]
    public async Task ArchiveFailureLeavesOriginalPaymentAndClockUntouchedAndCanBeRetried()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        await Enable(f);
        var origin = await Order(f);
        using var notice = PaymentNoticeFixture.Notice(origin.Origin);
        await Assert.ThrowsAsync<IOException>(() => Store(f).CaptureAsync(f.Context(0), notice, (_, _) => throw new IOException("private archive detail"), Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.payments WHERE first_payment_observed_at IS NULL AND ipn_signature IS NULL AND status='awaiting'", Ct));
        var archive = new Archive();
        Assert.Equal(PaymentCaptureOutcome.Ready, (await Store(f).CaptureAsync(f.Context(0), notice, archive.Save, Ct)).Outcome);
        await NoPosting(f);
    }

    [Fact]
    public async Task ZeroValueWaitingNoticeDoesNotPreventReplacingAnUnpaidOrder()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        await Enable(f);
        var original = await Order(f);
        using var notice = PaymentNoticeFixture.Notice(original.Origin, "waiting", 0);
        await Store(f).CaptureAsync(f.Context(0), notice, new Archive().Save, Ct);
        await Order(f, "987654321");
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='cancelled' AND resolution_kind='replaced'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='awaiting_payment'", Ct));
    }

    private static async Task Enable(CheckoutFixture f, bool zero = true)
    {
        await using var edit = await f.Sessions.BeginAsync(Ct);
        await edit.ExecuteAsync(new DbStatement("UPDATE shop.brands SET allow_crypto=true,allow_wallet_topup=true,zero_balance_topup=$1,policy_version=policy_version+1", DbValue.Boolean(zero)), Ct);
        await edit.ExecuteAsync(new DbStatement("UPDATE shop.brand_products SET allow_crypto=true,price_usdt=10.25,revision=revision+1"), Ct);
        await edit.CommitAsync(Ct);
    }
    private static async Task<(Guid Id, string Origin)> Order(CheckoutFixture f, string provider = "123456789")
    {
        var request = new CheckoutRequest(OrderIds.New(), f.Product, 1, CheckoutPayment.Crypto);
        var catalog = new CatalogStore(f.Sessions, f.Secrets, [], (_, _) => throw new InvalidOperationException());
        Assert.Equal(CheckoutOutcome.AwaitingPayment, (await new WalletCheckoutService(f.Sessions, f.Store, catalog).PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), request,
            (_, _, _) => Task.FromResult(ChannelMembership.Member), Ct)).Outcome);
        await using var bind = await f.Sessions.BeginAsync(Ct);
        var tuple = await bind.QuerySingleAsync(new DbStatement("SELECT id,idempotency_ref FROM shop.payments WHERE order_id=$1", DbValue.Uuid(request.OrderId)), r => (r.GetGuid(0), r.GetString(1)), Ct);
        await Store(f).BindInvoiceAsync(bind, f.Context(0), CheckoutFixture.Actor(0), tuple.Item1, provider, 10.4m, Ct);
        await bind.CommitAsync(Ct);
        return tuple;
    }
    private static async Task NoPosting(CheckoutFixture f)
    {
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit' OR movement_kind='wallet_topup' OR movement_kind LIKE '%crypto%credit'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.supplier_requests", Ct));
    }
}
