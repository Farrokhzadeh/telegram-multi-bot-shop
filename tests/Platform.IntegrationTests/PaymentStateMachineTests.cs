using Npgsql;
using Platform.Core;
using Platform.Persistence;
using Platform.Testing;

namespace Platform.IntegrationTests;

[Collection("PostgreSQL races")]
public sealed class PaymentStateMachineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HundredLateCallbacksCreditOneOriginalWalletAndNeverReviveOrRebindTheTerminalOrder(bool expired)
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, ct: Ct);
        var fixture = new PaymentSettlementFixture(f);
        await fixture.EnableAsync(Ct);
        var old = await fixture.OrderAsync(ct: Ct);
        if (expired)
        {
            await using var expire = await f.Sessions.BeginAsync(Ct);
            await expire.ExecuteAsync(new DbStatement("UPDATE shop.orders SET payment_status='expired',fulfillment_status='cancelled',resolution_kind='unpaid_expiry',resolution_at=clock_timestamp(),revision=revision+1"), Ct);
            await expire.CommitAsync(Ct);
        }
        else await fixture.OrderAsync(ct: Ct);
        using var limit = new SemaphoreSlim(8);
        var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(async _ =>
        {
            await limit.WaitAsync(Ct);
            try
            {
                await fixture.CaptureAsync(old.Payment, old.Origin, old.Due, ct: Ct);
                return await fixture.Store.SettleAsync(f.Context(0), old.Payment, PaymentSettlementFixture.Lookup, Ct);
            }
            finally { limit.Release(); }
        }));
        Assert.Single(results, r => r.Outcome == PaymentSettlementOutcome.Settled);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='late_crypto_credit' AND signed_amount=10", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallets WHERE balance=10 AND version=1", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='" + (expired ? "expired" : "cancelled") + "' AND resolution_kind='" + (expired ? "unpaid_expiry" : "replaced") + "'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.supplier_requests", Ct));
    }

    [Fact]
    public async Task LostCommitAcknowledgementReconcilesTheCommittedCreditInsteadOfPostingAgain()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, ct: Ct);
        var fixture = new PaymentSettlementFixture(f);
        await fixture.EnableAsync(Ct);
        var old = await fixture.OrderAsync(ct: Ct);
        await fixture.OrderAsync(ct: Ct);
        await fixture.CaptureAsync(old.Payment, old.Origin, old.Due, ct: Ct);
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SHOP_TEST_POSTGRES_ADMIN"))
        { Database = new NpgsqlConnectionStringBuilder(f.Database.Data.ConnectionString).Database, IncludeErrorDetail = false };
        await using var proxy = new DropCommitReplyProxy(connection.ConnectionString);
        await using var data = proxy.CreateClientDataSource();
        var error = await Assert.ThrowsAsync<DbOperationException>(() => new PaymentSettlementStore(new(data), f.Secrets, fixture.Catalog).SettleAsync(f.Context(0), old.Payment, PaymentSettlementFixture.Lookup, Ct));
        Assert.Equal(DbRecovery.ReconcileBeforeRetry, error.Failure.Recovery);
        await proxy.CommitObserved.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(PaymentSettlementOutcome.Replayed, (await fixture.Store.SettleAsync(f.Context(0), old.Payment, (_, _) => throw new InvalidOperationException(), Ct)).Outcome);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='late_crypto_credit'", Ct));
    }

    [Fact]
    public async Task DeferredCommitFailureRollsBackPaymentCreditAndWholeClaimReleaseTogether()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, ct: Ct);
        var fixture = new PaymentSettlementFixture(f);
        await fixture.EnableAsync(Ct);
        await Coupon(f, DateTime.UtcNow.AddHours(1));
        var order = await fixture.OrderAsync(ct: Ct);
        await fixture.CaptureAsync(order.Payment, order.Origin, order.Due, ct: Ct);
        await using (var fail = await f.Sessions.BeginAsync(Ct))
        {
            await fail.ExecuteAsync(new DbStatement("UPDATE shop.inventory_items SET state='retired'"), Ct);
            await fail.CommitAsync(Ct);
        }
        await using (var ddl = f.Database.Data.CreateCommand("CREATE FUNCTION shop.public_test_fail_settlement() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'public synthetic commit failure' USING ERRCODE='23514'; END $$; CREATE CONSTRAINT TRIGGER public_test_fail_settlement AFTER UPDATE ON shop.payments DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (NEW.status='settled') EXECUTE FUNCTION shop.public_test_fail_settlement()")) await ddl.ExecuteNonQueryAsync(Ct);
        var error = await Assert.ThrowsAsync<DbOperationException>(() => fixture.Store.SettleAsync(f.Context(0), order.Payment, PaymentSettlementFixture.Lookup, Ct));
        Assert.Equal(DbFailureKind.Constraint, error.Failure.Kind);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.payments WHERE status='confirming' AND compensating_wallet_entry_id IS NULL", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='held'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries", Ct));
        await using (var recover = f.Database.Data.CreateCommand("DROP TRIGGER public_test_fail_settlement ON shop.payments")) await recover.ExecuteNonQueryAsync(Ct);
        Assert.Equal(ExternalSettlementRoute.LocalShortageCredit, (await fixture.Store.SettleAsync(f.Context(0), order.Payment, PaymentSettlementFixture.Lookup, Ct)).Route);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='released'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE signed_amount=5", Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualCampaignExpiryPreservesOriginalGrantAndReturnsOnlyExpiredCapacityOnCompensation(bool shortage)
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, ct: Ct);
        var fixture = new PaymentSettlementFixture(f);
        await fixture.EnableAsync(Ct);
        DateTime ends;
        await using (var clock = await f.Sessions.BeginAsync(Ct)) ends = await clock.QuerySingleAsync(new DbStatement("SELECT clock_timestamp()+INTERVAL '5 seconds'"), r => r.GetUtc(0), Ct);
        Guid coupon = await Coupon(f, ends);
        var order = await fixture.OrderAsync(ct: Ct);
        Assert.Equal(5m, order.Due);
        await using (var cross = await f.Sessions.BeginAsync(Ct))
        {
            if (shortage) await cross.ExecuteAsync(new DbStatement("UPDATE shop.inventory_items SET state='retired'"), Ct);
            await cross.ExecuteAsync(new DbStatement("SELECT pg_sleep(GREATEST(0,EXTRACT(EPOCH FROM ($1::timestamptz-clock_timestamp())))+0.01)", DbValue.Utc(ends)), Ct);
            await cross.CommitAsync(Ct);
        }
        await fixture.CaptureAsync(order.Payment, order.Origin, order.Due, ct: Ct);
        await fixture.Store.SettleAsync(f.Context(0), order.Payment, PaymentSettlementFixture.Lookup, Ct);
        if (!shortage)
        {
            JobLease lease;
            await using (var claim = await f.Sessions.BeginAsync(Ct))
            { lease = (await OutboxStore.ClaimAsync(claim, JobKind.PrepareManifest, Guid.NewGuid(), TimeSpan.FromSeconds(30), Ct))!; await claim.CommitAsync(Ct); }
            await new DeliveryStore(f.Sessions, f.Secrets).PrepareAsync(f.Context(0), lease, Ct);
            Assert.Equal(1L, await f.CountAsync("SELECT redeemed_count FROM shop.discounts", Ct));
            Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='redeemed'", Ct));
            Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", Ct));
        }
        else
        {
            await using var read = await f.Sessions.BeginAsync(Ct);
            var view = await PaymentReadStore.ForCustomerAsync(read, f.Context(0), CheckoutFixture.Actor(0), order.Payment, Ct);
            Assert.True(view!.ReturnedCouponExpired);
            Assert.Equal("5", view.WalletCreditUsd);
            Assert.Equal(0L, await f.CountAsync("SELECT redeemed_count FROM shop.discounts", Ct));
            await f.AddStockAsync(1, Ct);
            var quote = await fixture.Catalog.QuoteAsync(f.Context(0), CheckoutFixture.Actor(0), new(f.Product, 1, CheckoutPayment.Crypto), Ct);
            Assert.Null(quote.Terms.DiscountId);
            Assert.Equal(10m, quote.TotalUsd.Amount);
        }
        await using var verify = await f.Sessions.BeginAsync(Ct);
        Assert.Equal(ends, await verify.QuerySingleAsync(new DbStatement("SELECT ends_at FROM shop.discounts WHERE id=$1", DbValue.Uuid(coupon)), r => r.GetUtc(0), Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discounts", Ct));
    }
    [Fact]
    public async Task VerifiedStandaloneFundingCreatesOnlyItsOriginalMissingWallet()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, stock: 0, ct: Ct);
        var fixture = new PaymentSettlementFixture(f);
        await fixture.EnableAsync(Ct);
        await using (var observe = await f.Sessions.BeginAsync(Ct))
        { await BrandUserStore.ObservePrivateAsync(observe, f.Context(0), 9999, Ct); await observe.CommitAsync(Ct); }
        Guid payment = Guid.NewGuid();
        var evidence = new PaymentEvidenceStore(f.Sessions, f.Secrets);
        await evidence.CreateTopupAsync(f.Context(0), 9999, payment, new(20, Currency.Usd), CryptoAsset.UsdtTrc20, Ct);
        await using (var bind = await f.Sessions.BeginAsync(Ct))
        { await evidence.BindInvoiceAsync(bind, f.Context(0), 9999, payment, PaymentSettlementFixture.Provider(payment), 10.4m, Ct); await bind.CommitAsync(Ct); }
        await fixture.CaptureAsync(payment, "topup:" + payment.ToString("D"), 20, ct: Ct);
        await fixture.Store.SettleAsync(f.Context(0), payment, PaymentSettlementFixture.Lookup, Ct);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallets w JOIN shop.users u ON u.id=w.user_id WHERE u.telegram_id=9999 AND w.balance=20 AND w.version=1", Ct));
        Assert.Equal(3L, await f.CountAsync("SELECT count(*) FROM shop.wallets WHERE balance=0", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders", Ct));
    }
    private static async Task<Guid> Coupon(CheckoutFixture f, DateTime ends)
    {
        Guid coupon = Guid.NewGuid();
        var admin = await f.AdminAsync(Ct);
        await using var issue = await f.Sessions.BeginAsync(Ct);
        await new PricingStore(f.Sessions, f.Secrets).IssueAsync(issue, f.Context(0), admin, new(coupon, "PUBLIC-" + coupon.ToString("N"), CouponKind.Promotion, 50, f.Product, f.Users[0], MaximumUses: 1, EndsAt: ends), Guid.NewGuid(), Ct);
        await issue.CommitAsync(Ct);
        return coupon;
    }
}
