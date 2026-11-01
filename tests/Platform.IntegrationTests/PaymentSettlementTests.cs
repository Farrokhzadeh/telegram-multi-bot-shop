using Npgsql;
using Platform.Core;
using Platform.Persistence;
using Platform.Telegram;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class PaymentSettlementTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task ActualLocalSettlementIsAtomicAndReplaySkipsProviderTransport() => await PaymentSettlementFixture.RunNativeAsync(Ct);

    [Fact]
    public async Task HundredConcurrentTopupSettlementsCreditOriginalMembershipOnceDespiteCurrentInitiationPolicy()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, stock: 0, ct: Ct);
        var fixture = new PaymentSettlementFixture(f);
        await fixture.EnableAsync(Ct);
        Guid payment = Guid.NewGuid();
        var evidence = new PaymentEvidenceStore(f.Sessions, f.Secrets);
        await evidence.CreateTopupAsync(f.Context(0), CheckoutFixture.Actor(0), payment, new(10.25m, Currency.Usd), CryptoAsset.UsdtBsc, Ct);
        await using (var bind = await f.Sessions.BeginAsync(Ct))
        { await evidence.BindInvoiceAsync(bind, f.Context(0), CheckoutFixture.Actor(0), payment, PaymentSettlementFixture.Provider(payment), 10.4m, Ct); await bind.CommitAsync(Ct); }
        using (var notice = PaymentNoticeFixture.Notice("topup:" + payment.ToString("D"), asset: "usdtbsc", provider: PaymentSettlementFixture.Provider(payment)))
            await evidence.CaptureAsync(f.Context(0), notice, (_, _) => Task.FromResult("protected:synthetic/topup"), Ct);
        await using (var disable = await f.Sessions.BeginAsync(Ct))
        {
            await disable.ExecuteAsync(new DbStatement("UPDATE shop.brands SET allow_wallet_topup=false,zero_balance_topup=false,policy_version=policy_version+1"), Ct);
            await disable.ExecuteAsync(new DbStatement("UPDATE shop.users SET blocked_at=clock_timestamp() WHERE id=$1", DbValue.Uuid(f.Users[0])), Ct);
            await disable.CommitAsync(Ct);
        }
        var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SHOP_TEST_POSTGRES_ADMIN"))
        { Database = new NpgsqlConnectionStringBuilder(f.Database.Data.ConnectionString).Database, MaxPoolSize = 16, IncludeErrorDetail = false };
        await using var bounded = new NpgsqlSlimDataSourceBuilder(options.ConnectionString).Build();
        var sessions = new DbSessionFactory(bounded);
        var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => new PaymentSettlementStore(sessions, f.Secrets, fixture.Catalog).SettleAsync(f.Context(0), payment, PaymentSettlementFixture.Lookup, Ct)));
        Assert.Single(results, r => r.Outcome == PaymentSettlementOutcome.Settled);
        Assert.Equal(99, results.Count(r => r.Outcome == PaymentSettlementOutcome.Replayed));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='wallet_topup' AND signed_amount=10.25 AND posting_version=1", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallets WHERE balance=10.25 AND version=1", Ct));
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.wallets WHERE balance=0", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders", Ct));
    }

    [Fact]
    public async Task ReadyEvidenceNeedsMatchingAuthenticatedFinishedLookupAndForeignBrandCannotLookup()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, ct: Ct);
        var fixture = new PaymentSettlementFixture(f);
        await fixture.EnableAsync(Ct);
        var order = await fixture.OrderAsync(ct: Ct);
        Assert.Equal(PaymentSettlementOutcome.Pending, (await fixture.Store.SettleAsync(f.Context(0), order.Payment, (_, _) => throw new InvalidOperationException(), Ct)).Outcome);
        await fixture.CaptureAsync(order.Payment, order.Origin, order.Due, ct: Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Store.SettleAsync(f.Context(1), order.Payment, (_, _) => throw new InvalidOperationException(), Ct));
        using var client = PaymentSettlementFixture.Client(_ => PaymentNoticeFixture.Body(order.Origin, "confirmed", quote: order.Due, provider: PaymentSettlementFixture.Provider(order.Payment)));
        Assert.Equal(PaymentSettlementOutcome.Pending, (await fixture.Store.SettleAsync(f.Context(0), order.Payment, client.LookupAsync, Ct)).Outcome);
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments", Ct));
        Assert.Equal(PaymentSettlementOutcome.Settled, (await fixture.Store.SettleAsync(f.Context(0), order.Payment, PaymentSettlementFixture.Lookup, Ct)).Outcome);
    }

    [Theory]
    [InlineData("usdtbsc", "10.4", "finished")]
    [InlineData("usdttrc20", "1", "finished")]
    [InlineData("usdttrc20", "11", "finished")]
    [InlineData("usdttrc20", "10.4", "unknown")]
    public async Task ConflictingLookupLeavesStickyReviewAndNoFinancialEffects(string asset, string tokens, string status)
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, ct: Ct);
        var fixture = new PaymentSettlementFixture(f);
        await fixture.EnableAsync(Ct);
        var order = await fixture.OrderAsync(ct: Ct);
        await fixture.CaptureAsync(order.Payment, order.Origin, order.Due, ct: Ct);
        using var client = PaymentSettlementFixture.Client(_ => PaymentNoticeFixture.Body(order.Origin, status, ExactDecimal.Parse(tokens), asset, order.Due, PaymentSettlementFixture.Provider(order.Payment)));
        Assert.Equal(PaymentSettlementOutcome.Review, (await fixture.Store.SettleAsync(f.Context(0), order.Payment, client.LookupAsync, Ct)).Outcome);
        Assert.Equal(PaymentSettlementOutcome.Review, (await fixture.Store.SettleAsync(f.Context(0), order.Payment, PaymentSettlementFixture.Lookup, Ct)).Outcome);
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", Ct));
    }

    [Fact]
    public async Task LookupFailureRetainsReadyEvidenceAndPaidLiabilityForRetry()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, ct: Ct);
        var fixture = new PaymentSettlementFixture(f);
        await fixture.EnableAsync(Ct);
        var order = await fixture.OrderAsync(ct: Ct);
        await fixture.CaptureAsync(order.Payment, order.Origin, order.Due, ct: Ct);
        await Assert.ThrowsAsync<IOException>(() => fixture.Store.SettleAsync(f.Context(0), order.Payment, (_, _) => throw new IOException(), Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.payments WHERE status='confirming' AND amount_received=amount_due", Ct));
        Assert.Equal(PaymentSettlementOutcome.Settled, (await fixture.Store.SettleAsync(f.Context(0), order.Payment, PaymentSettlementFixture.Lookup, Ct)).Outcome);
    }

    [Fact]
    public async Task LocalShortageCreditsFrozenNetQuoteAndReleasesTheWholeProtectedClaim()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, stock: 1, ct: Ct);
        var fixture = new PaymentSettlementFixture(f);
        await fixture.EnableAsync(Ct);
        Guid coupon = await Coupon(f, 50, 1);
        var order = await fixture.OrderAsync(ct: Ct);
        Assert.Equal(5m, order.Due);
        await using (var retire = await f.Sessions.BeginAsync(Ct))
        { await retire.ExecuteAsync(new DbStatement("UPDATE shop.inventory_items SET state='retired'"), Ct); await retire.CommitAsync(Ct); }
        await fixture.CaptureAsync(order.Payment, order.Origin, order.Due, ct: Ct);
        Assert.Equal(ExternalSettlementRoute.LocalShortageCredit, (await fixture.Store.SettleAsync(f.Context(0), order.Payment, PaymentSettlementFixture.Lookup, Ct)).Route);
        Assert.Equal(PaymentSettlementOutcome.Replayed, (await fixture.Store.SettleAsync(f.Context(0), order.Payment, PaymentSettlementFixture.Lookup, Ct)).Outcome);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='immediate_crypto_shortage_credit' AND signed_amount=5", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='released' AND units_covered=1", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT redeemed_count FROM shop.discounts", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='paid' AND fulfillment_status='compensated'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE purpose='order_status'", Ct));
        Assert.NotEqual(Guid.Empty, coupon);
    }

    [Fact]
    public async Task ReplacedInvoiceCreditsOriginalWalletWithoutRevivalOrOverwritingOriginalResolution()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, ct: Ct);
        var fixture = new PaymentSettlementFixture(f);
        await fixture.EnableAsync(Ct);
        var old = await fixture.OrderAsync(ct: Ct);
        await fixture.OrderAsync(ct: Ct);
        await fixture.CaptureAsync(old.Payment, old.Origin, old.Due, ct: Ct);
        var result = await fixture.Store.SettleAsync(f.Context(0), old.Payment, PaymentSettlementFixture.Lookup, Ct);
        Assert.Equal(ExternalSettlementRoute.LateCredit, result.Route);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='cancelled' AND fulfillment_status='cancelled' AND resolution_kind='replaced' AND resolution_wallet_entry_id IS NULL", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='awaiting_payment'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='late_crypto_credit' AND signed_amount=10", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments", Ct));
    }

    [Fact]
    public async Task PrepaidSettlementIssuesOneOwnedNonExpiringEntitlementAndNoGoods()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, stock: 0, ct: Ct);
        var fixture = new PaymentSettlementFixture(f);
        await fixture.EnableAsync(Ct);
        var order = await fixture.OrderAsync(100, CheckoutKind.PrepaidUnits, ct: Ct);
        await fixture.CaptureAsync(order.Payment, order.Origin, order.Due, ct: Ct);
        Assert.Equal(ExternalSettlementRoute.Prepaid, (await fixture.Store.SettleAsync(f.Context(0), order.Payment, PaymentSettlementFixture.Lookup, Ct)).Route);
        await fixture.Store.SettleAsync(f.Context(0), order.Payment, PaymentSettlementFixture.Lookup, Ct);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discounts WHERE kind='prepaid_entitlement' AND max_total_uses=100 AND percent_off=100 AND ends_at IS NULL", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", Ct));
    }

    [Fact]
    public async Task LocalPreorderRetainsPaidFifoAndHeldCouponWithoutAutomaticCredit()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, stock: 0, ct: Ct);
        var fixture = new PaymentSettlementFixture(f);
        await fixture.EnableAsync(Ct);
        await using (var edit = await f.Sessions.BeginAsync(Ct))
        { await edit.ExecuteAsync(new DbStatement("UPDATE shop.products SET availability_policy='local_preorder',revision=revision+1"), Ct); await edit.CommitAsync(Ct); }
        await Coupon(f, 50, 5);
        var order = await fixture.OrderAsync(5, ct: Ct);
        await fixture.CaptureAsync(order.Payment, order.Origin, order.Due, ct: Ct);
        Assert.Equal(ExternalSettlementRoute.LocalPreorder, (await fixture.Store.SettleAsync(f.Context(0), order.Payment, PaymentSettlementFixture.Lookup, Ct)).Route);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE is_preorder AND payment_status='paid' AND fulfillment_status='waiting_stock'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='held' AND units_covered=5", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedSupplierSettlementBindsCurrentSourceOrPreservesBlockedPaidObligation(bool disable)
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, stock: 3, ct: Ct);
        var fixture = new PaymentSettlementFixture(f);
        await fixture.EnableAsync(Ct);
        await using (var edit = await f.Sessions.BeginAsync(Ct))
        {
            Guid source = Guid.NewGuid();
            await edit.ExecuteAsync(new DbStatement("INSERT INTO shop.product_supplier_sources(id,product_id,provider_code,external_product_id,enabled) VALUES ($1,$2,'synthetic','public product',true)", DbValue.Uuid(source), DbValue.Uuid(f.Product)), Ct);
            await edit.ExecuteAsync(new DbStatement("UPDATE shop.products SET fulfillment_kind='supplier',active_supplier_source_id=$1,revision=revision+1", DbValue.Uuid(source)), Ct);
            await edit.CommitAsync(Ct);
        }
        await Coupon(f, 50, 5);
        var order = await fixture.OrderAsync(5, ct: Ct);
        if (disable)
        {
            await using var edit = await f.Sessions.BeginAsync(Ct);
            await edit.ExecuteAsync(new DbStatement("UPDATE shop.product_supplier_sources SET enabled=false,revision=revision+1"), Ct);
            await edit.CommitAsync(Ct);
        }
        await fixture.CaptureAsync(order.Payment, order.Origin, order.Due, ct: Ct);
        Assert.Equal(ExternalSettlementRoute.Supplier, (await fixture.Store.SettleAsync(f.Context(0), order.Payment, PaymentSettlementFixture.Lookup, Ct)).Route);
        Assert.Equal(3L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments WHERE status='acquired'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.supplier_requests WHERE requested_quantity=2 AND cost_baseline_amount=1 AND status='" + (disable ? "blocked" : "pending") + "'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='held'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE release_authorized_at IS NOT NULL", Ct));
    }

    private static async Task<Guid> Coupon(CheckoutFixture f, decimal percent, int quantity)
    {
        Guid id = Guid.NewGuid();
        var admin = await f.AdminAsync(Ct);
        await using var issue = await f.Sessions.BeginAsync(Ct);
        await new PricingStore(f.Sessions, f.Secrets).IssueAsync(issue, f.Context(0), admin, new(id, "PUBLIC-" + id.ToString("N"), CouponKind.Promotion, percent, f.Product, f.Users[0], MaximumUses: quantity), Guid.NewGuid(), Ct);
        await issue.CommitAsync(Ct);
        return id;
    }
}
