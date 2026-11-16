using Platform.Core;
using Platform.Persistence;
using Platform.Telegram;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class PreparedCheckoutTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static CatalogStore Catalog(CheckoutFixture f, Func<SupplierCatalogRequest, CancellationToken, Task<SupplierCatalogResult>>? fetch = null) =>
        new(f.Sessions, f.Secrets, [new("synthetic", "public account")], fetch ?? ((_, _) => throw new InvalidOperationException("Unexpected transport.")));
    private static WalletCheckoutService Service(CheckoutFixture f, CatalogStore? catalog = null) => new(f.Sessions, f.Store, catalog ?? Catalog(f));
    private static Task<ChannelMembership> Member(long chat, long actor, CancellationToken ct) => Task.FromResult(ChannelMembership.Member);
    private static CheckoutRequest Request(CheckoutFixture f, int qty = 1, CheckoutPayment payment = CheckoutPayment.Wallet) => new(OrderIds.New(), f.Product, qty, payment);

    [Theory]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(100)]
    public async Task WholeDiscountedLocalAcceptanceRedeemsWithOneExactDebitOrNoMoney(int percent)
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: percent == 100 ? 0 : 100, stock: 11, ct: Ct);
        Guid coupon = await Coupon(f, percent, 10);
        var service = Service(f);
        var request = Request(f, 10);
        var invalid = await Assert.ThrowsAsync<DiscountQuantityException>(() => service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), Request(f, 11), Member, Ct));
        Assert.Equal(10, invalid.Maximum);
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders", Ct));
        Assert.Equal(CheckoutOutcome.Purchased, (await service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), request, Member, Ct)).Outcome);
        Assert.Equal(CheckoutOutcome.Replayed, (await service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), request, (_, _, _) => throw new InvalidOperationException("Replay transport."), Ct)).Outcome);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='redeemed' AND units_covered=10", Ct));
        Assert.Equal(10L, await f.CountAsync("SELECT redeemed_count FROM shop.discounts", Ct));
        Assert.Equal(10L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments WHERE status='acquired'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE release_authorized_at IS NOT NULL", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='prepare_manifest'", Ct));
        Assert.Equal(percent == 100 ? 0L : 1L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", Ct));
        await using var read = await f.Sessions.BeginAsync(Ct);
        decimal balance = await read.QuerySingleAsync(new DbStatement("SELECT balance::text FROM shop.wallets WHERE user_id=$1", DbValue.Uuid(f.Users[0])), r => ExactDecimal.Parse(r.GetString(0)), Ct);
        Assert.Equal(percent == 100 ? 0m : percent, balance);
        Assert.Equal(coupon, await read.QuerySingleAsync(new DbStatement("SELECT discount_id FROM shop.discount_uses WHERE order_id=$1", DbValue.Uuid(request.OrderId)), r => r.GetGuid(0), Ct));
    }

    [Fact]
    public async Task FreeMixedAcceptanceBindsOnlyTheRemoteRemainderAndReplayNeverRefreshesOrRebinds()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, stock: 3, ct: Ct);
        Guid source = await Supplier(f);
        await Coupon(f, 100, 5);
        int calls = 0;
        var catalog = Catalog(f, (request, _) =>
        {
            calls++;
            Assert.Equal(2, request.Quantity);
            return Task.FromResult(new SupplierCatalogResult(SupplierCatalogStatus.Available, 2, 7, "USD", "public cost quote"));
        });
        var service = Service(f, catalog);
        var order = Request(f, 5);
        Assert.Equal(CheckoutOutcome.Purchased, (await service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), order, Member, Ct)).Outcome);
        await using (var edit = await f.Sessions.BeginAsync(Ct))
        {
            await edit.ExecuteAsync(new DbStatement("UPDATE shop.product_supplier_sources SET enabled=false,revision=revision+1 WHERE id=$1", DbValue.Uuid(source)), Ct);
            await edit.ExecuteAsync(new DbStatement("UPDATE shop.brand_products SET visible=false,revision=revision+1 WHERE brand_id=$1", DbValue.Uuid(f.Brands[0])), Ct);
            await edit.CommitAsync(Ct);
        }
        Assert.Equal(CheckoutOutcome.Replayed, (await service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), order, Member, Ct)).Outcome);
        Assert.Equal(1, calls);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='not_required' AND payment_method='no_payment_required' AND payment_due_at IS NULL", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.supplier_requests WHERE requested_quantity=2 AND status='pending' AND account_ref='public account' AND cost_baseline_amount=7", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='submit_supplier'", Ct));
        Assert.Equal(3L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments WHERE kind='local' AND status='acquired'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE units_covered=5 AND status='held'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.payments", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries", Ct));
    }

    [Fact]
    public async Task WalletMixedAcceptanceUsesRetailDebitAndFailedRefreshHasZeroEffects()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 3, ct: Ct);
        await Supplier(f);
        await Assert.ThrowsAsync<PricingRejectedException>(() => Service(f, Catalog(f, (_, _) => throw new HttpRequestException())).PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), Request(f, 5), Member, Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders", Ct));
        var service = Service(f, Catalog(f, (_, _) => Task.FromResult(new SupplierCatalogResult(SupplierCatalogStatus.Available, 2, 99, "USD", "public quote"))));
        Assert.Equal(CheckoutOutcome.Purchased, (await service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), Request(f, 5), Member, Ct)).Outcome);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit' AND signed_amount=-50", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.supplier_requests WHERE requested_quantity=2 AND cost_baseline_amount=99", Ct));
    }

    [Fact]
    public async Task WholeLocalSupplierCoverageNeedsNoTransportOrRemoteBinding()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 5, ct: Ct);
        await Supplier(f);
        Assert.Equal(CheckoutOutcome.Purchased, (await Service(f).PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), Request(f, 5), Member, Ct)).Outcome);
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.supplier_requests", Ct));
        Assert.Equal(5L, await f.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='allocated'", Ct));
    }

    [Fact]
    public async Task PaidPreordersWaitWholeBehindFifoAndHoldTheirCouponWithoutAllocating()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 1, ct: Ct);
        await using (var change = await f.Sessions.BeginAsync(Ct))
        {
            await change.ExecuteAsync(new DbStatement("UPDATE shop.products SET availability_policy='local_preorder',revision=revision+1 WHERE id=$1", DbValue.Uuid(f.Product)), Ct);
            await change.CommitAsync(Ct);
        }
        await Coupon(f, 50, 10);
        var service = Service(f);
        Assert.Equal(CheckoutOutcome.Purchased, (await service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), Request(f, 2), Member, Ct)).Outcome);
        Assert.Equal(CheckoutOutcome.Purchased, (await service.PurchaseAsync(f.Context(1), CheckoutFixture.Actor(1), Request(f), Member, Ct)).Outcome);
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE fulfillment_status='waiting_stock' AND is_preorder AND payment_status='paid'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='available'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='held' AND units_covered=2", Ct));
    }

    [Fact]
    public async Task ChannelDenialForeignReplayAndChangedInputCannotCreateOrInspectAnOrder()
    {
        await using var f = await CheckoutFixture.CreateAsync(users: 6, stock: 3, ct: Ct);
        await using (var channel = await f.Sessions.BeginAsync(Ct))
        {
            await channel.ExecuteAsync(new DbStatement("UPDATE shop.brands SET require_channel_join=true,policy_version=policy_version+1 WHERE id=$1", DbValue.Uuid(f.Brands[0])), Ct);
            await channel.ExecuteAsync(new DbStatement("INSERT INTO shop.brand_channels(brand_id,chat_id,required,active) VALUES ($1,-1001,true,true)", DbValue.Uuid(f.Brands[0])), Ct);
            await channel.CommitAsync(Ct);
        }
        var service = Service(f);
        var order = Request(f);
        Assert.Equal(CheckoutOutcome.Unavailable, (await service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), order, (_, _, _) => Task.FromResult(ChannelMembership.Left), Ct)).Outcome);
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders", Ct));
        Assert.Equal(CheckoutOutcome.Purchased, (await service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), order, Member, Ct)).Outcome);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PurchaseAsync(f.Context(1), CheckoutFixture.Actor(1), order, Member, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PurchaseAsync(f.Context(3), CheckoutFixture.Actor(3), order, Member, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), order with { Quantity = 2 }, Member, Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders", Ct));
    }

    [Fact]
    public async Task CipherFailureAfterHoldAndDebitRollsBackTheWholeAcceptanceAndAllowsCleanRetry()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 2, ct: Ct);
        await Coupon(f, 50, 10);
        byte[] content;
        Guid item;
        await using (var breakCipher = await f.Sessions.BeginAsync(Ct))
        {
            (item, content) = await breakCipher.QuerySingleAsync(new DbStatement("SELECT id,credential_ciphertext FROM shop.inventory_items ORDER BY id LIMIT 1"), r => (r.GetGuid(0), r.GetBinary(1)), Ct);
            await breakCipher.ExecuteAsync(new DbStatement("UPDATE shop.inventory_items SET credential_ciphertext=decode('01','hex') WHERE id=$1", DbValue.Uuid(item)), Ct);
            await breakCipher.CommitAsync(Ct);
        }
        var service = Service(f);
        var request = Request(f, 2);
        await Assert.ThrowsAsync<CipherRejectedException>(() => service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), request, Member, Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", Ct));
        await using (var repair = await f.Sessions.BeginAsync(Ct))
        {
            await repair.ExecuteAsync(new DbStatement("UPDATE shop.inventory_items SET credential_ciphertext=$1 WHERE id=$2", DbValue.Binary(content), DbValue.Uuid(item)), Ct);
            await repair.CommitAsync(Ct);
        }
        Assert.Equal(CheckoutOutcome.Purchased, (await service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), request, Member, Ct)).Outcome);
    }

    [Fact]
    public async Task DeferredRedemptionFailureRollsBackLocalAllocationCouponAndDebit()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 2, ct: Ct);
        await Coupon(f, 50, 10);
        await using (var install = f.Database.Data.CreateCommand("CREATE FUNCTION shop.public_test_redemption_failure() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'public synthetic redemption failure' USING ERRCODE='23514'; END $$; CREATE CONSTRAINT TRIGGER public_test_redemption_failure AFTER UPDATE ON shop.discount_uses DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (NEW.status='redeemed') EXECUTE FUNCTION shop.public_test_redemption_failure()")) await install.ExecuteNonQueryAsync(Ct);
        var request = Request(f, 2);
        await Assert.ThrowsAsync<DbOperationException>(() => Service(f).PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), request, Member, Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT redeemed_count FROM shop.discounts", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", Ct));
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='available'", Ct));
        await using (var remove = f.Database.Data.CreateCommand("DROP TRIGGER public_test_redemption_failure ON shop.discount_uses")) await remove.ExecuteNonQueryAsync(Ct);
        Assert.Equal(CheckoutOutcome.Purchased, (await Service(f).PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), request, Member, Ct)).Outcome);
        Assert.Equal(2L, await f.CountAsync("SELECT redeemed_count FROM shop.discounts", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", Ct));
    }

    [Fact]
    public async Task ValidWalletCheckoutReplacesOnlyAnUnobservedPendingOrderAndCancelsItsExpiry()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 2, ct: Ct);
        Guid old = await Pending(f, "awaiting_payment");
        await using (var job = await f.Sessions.BeginAsync(Ct))
        {
            await OutboxStore.EnqueueAsync(job, JobIntent.Order(JobKind.ExpireOrderPayment, f.Brands[0], old), old, DateTime.UtcNow, Ct);
            await job.CommitAsync(Ct);
        }
        Assert.Equal(CheckoutOutcome.Purchased, (await Service(f).PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), Request(f), Member, Ct)).Outcome);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE resolution_kind='replaced' AND payment_status='cancelled'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='expire_order_payment' AND status='cancelled' AND completed_at IS NOT NULL", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", Ct));
    }

    [Fact]
    public async Task PendingReviewAndInsufficientFundsPreserveExistingObligationsWithoutNewEffects()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, stock: 2, ct: Ct);
        await Pending(f, "awaiting_payment");
        Assert.Equal(CheckoutOutcome.InsufficientBalance, (await Service(f).PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), Request(f), Member, Ct)).Outcome);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='awaiting_payment' AND resolution_kind IS NULL", Ct));
        await using (var review = await f.Sessions.BeginAsync(Ct))
        {
            await review.ExecuteAsync(new DbStatement("UPDATE shop.orders SET payment_status='under_review',revision=revision+1"), Ct);
            await review.CommitAsync(Ct);
        }
        Assert.Equal(CheckoutOutcome.InsufficientBalance, (await Service(f).PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), Request(f), Member, Ct)).Outcome);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='under_review'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", Ct));
    }

    [Fact]
    public async Task ConcurrentFreeAcceptancesCannotOversubscribeOneSharedCouponUnit()
    {
        await using var f = await CheckoutFixture.CreateAsync(users: 6, balance: 0, stock: 2, ct: Ct);
        var admin = await f.AdminAsync(Ct);
        await using (var issue = await f.Sessions.BeginAsync(Ct))
        {
            Guid id = Guid.NewGuid();
            await new PricingStore(f.Sessions, f.Secrets).IssueAsync(issue, f.Context(0), admin,
                new(id, "PUBLIC-RACE", CouponKind.Promotion, 100, f.Product, MaximumUses: 1), Guid.NewGuid(), Ct);
            await issue.CommitAsync(Ct);
        }
        var catalog = Catalog(f);
        var service = Service(f, catalog);
        var a = await catalog.PrepareAsync(f.Context(0), CheckoutFixture.Actor(0), new(f.Product, 1, CheckoutPayment.Wallet), Ct);
        var b = await catalog.PrepareAsync(f.Context(3), CheckoutFixture.Actor(3), new(f.Product, 1, CheckoutPayment.Wallet), Ct);
        ChannelAccess accessA = await Access(f, 0), accessB = await Access(f, 3);
        async Task<bool> Buy(int user, PreparedCheckout prepared, ChannelAccess access)
        {
            try { return (await service.AcceptAsync(f.Context(user), CheckoutFixture.Actor(user), Request(f), prepared, access, Ct)).Outcome == CheckoutOutcome.Purchased; }
            catch (DiscountQuantityException error) { Assert.Equal(0, error.Maximum); return false; }
        }
        var results = await Task.WhenAll(Buy(0, a, accessA), Buy(3, b, accessB));
        Assert.Single(results, success => success);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='redeemed'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='allocated'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", Ct));
    }

    [Fact]
    public async Task ChangedChannelPolicyBetweenProofAndAcceptanceHasZeroEffects()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 1, ct: Ct);
        var catalog = Catalog(f);
        var proof = await catalog.PrepareAsync(f.Context(0), CheckoutFixture.Actor(0), new(f.Product, 1, CheckoutPayment.Wallet), Ct);
        ChannelAccess access = await Access(f, 0);
        await using (var edit = await f.Sessions.BeginAsync(Ct))
        {
            await edit.ExecuteAsync(new DbStatement("UPDATE shop.brands SET require_channel_join=true,policy_version=policy_version+1 WHERE id=$1", DbValue.Uuid(f.Brands[0])), Ct);
            await edit.CommitAsync(Ct);
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(f, catalog).AcceptAsync(f.Context(0), CheckoutFixture.Actor(0), Request(f), proof, access, Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='available'", Ct));
    }

    private static async Task<ChannelAccess> Access(CheckoutFixture f, int user)
    {
        BrandPolicySnapshot snapshot;
        await using (var read = await f.Sessions.BeginAsync(Ct)) snapshot = await BrandPolicyStore.ReadAsync(read, f.Context(user), CheckoutFixture.Actor(user), Ct);
        return (await BrandPolicy.CheckChannelsAsync(snapshot, CheckoutFixture.Actor(user), Member, Ct))!;
    }

    private static async Task<Guid> Pending(CheckoutFixture f, string status)
    {
        Guid id = OrderIds.New();
        await using var seed = await f.Sessions.BeginAsync(Ct);
        await seed.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.orders(id,user_id,product_id,quantity,order_kind,payment_status,currency,payment_method,unit_price_usdt,
                auto_price_toman_snapshot,pricing_rounding_step_snapshot,price_calculation_version,brand_public_name_snapshot,product_title_snapshot,
                payment_terms_snapshot,fulfillment_kind_snapshot,local_first_snapshot,is_preorder,payment_due_at)
            VALUES ($1,$2,$3,1,'product_purchase',$4,'USDT','crypto',10,false,5000,'public pending fixture','public brand','public title',
                'public terms','local',true,false,CURRENT_TIMESTAMP+INTERVAL '30 minutes')
            """, DbValue.Uuid(id), DbValue.Uuid(f.Users[0]), DbValue.Uuid(f.Product), DbValue.Text(status)), Ct);
        await seed.CommitAsync(Ct);
        return id;
    }

    private static async Task<Guid> Coupon(CheckoutFixture f, decimal percent, long cap)
    {
        Guid id = Guid.NewGuid();
        var admin = await f.AdminAsync(Ct);
        await using var issue = await f.Sessions.BeginAsync(Ct);
        await new PricingStore(f.Sessions, f.Secrets).IssueAsync(issue, f.Context(0), admin, new(id, "PUBLIC-" + id.ToString("N"), CouponKind.Promotion, percent, f.Product, f.Users[0], MaximumUses: cap), Guid.NewGuid(), Ct);
        await issue.CommitAsync(Ct);
        return id;
    }
    private static async Task<Guid> Supplier(CheckoutFixture f)
    {
        Guid source = Guid.NewGuid();
        await using var change = await f.Sessions.BeginAsync(Ct);
        await change.ExecuteAsync(new DbStatement("INSERT INTO shop.product_supplier_sources(id,product_id,provider_code,external_product_id,enabled) VALUES ($1,$2,'synthetic','public product',true)", DbValue.Uuid(source), DbValue.Uuid(f.Product)), Ct);
        await change.ExecuteAsync(new DbStatement("UPDATE shop.products SET fulfillment_kind='supplier',active_supplier_source_id=$1,revision=revision+1 WHERE id=$2", DbValue.Uuid(source), DbValue.Uuid(f.Product)), Ct);
        await change.CommitAsync(Ct);
        return source;
    }
}
