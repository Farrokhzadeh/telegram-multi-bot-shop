using Platform.Core;
using Platform.Persistence;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class CatalogQuoteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static CatalogStore Store(CheckoutFixture f, Func<SupplierCatalogRequest, CancellationToken, Task<SupplierCatalogResult>>? fetch = null) =>
        new(f.Sessions, f.Secrets, [new("synthetic", "public-synthetic-account")], fetch ?? ((_, _) => throw new InvalidOperationException("Unexpected supplier call.")));

    [Theory]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(100)]
    public async Task AutomaticWholeQuantityCapsRejectBothCurrenciesWithoutOrderOrFinancialEffects(int percent)
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, stock: 20, ct: Ct);
        await Bank(f);
        Guid coupon = await Coupon(f, percent, 10, f.Users[0]);
        var catalog = Store(f);
        foreach (CheckoutPayment payment in new[] { CheckoutPayment.Wallet, CheckoutPayment.Crypto, CheckoutPayment.Bank })
        {
            var error = await Assert.ThrowsAsync<DiscountQuantityException>(() => catalog.QuoteAsync(f.Context(0), CheckoutFixture.Actor(0), new(f.Product, 11, payment), Ct));
            Assert.Equal(10, error.Maximum);
            var quote = await catalog.QuoteAsync(f.Context(0), CheckoutFixture.Actor(0), new(f.Product, 10, payment), Ct);
            Assert.Equal(coupon, quote.Terms.DiscountId);
            Assert.Equal(100m - percent, quote.TotalUsd.Amount);
            Assert.Equal(10000000m * (100 - percent) / 100, quote.TotalToman!.Value.Amount);
            if (percent == 100) Assert.Equal(CheckoutPayment.NoPaymentRequired, quote.Terms.Payment);
        }
        await NoMoney(f);
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses", Ct));
    }

    [Fact]
    public async Task PresentationAndCouponEligibilityRemainScopedToTheOriginalMembership()
    {
        await using var f = await CheckoutFixture.CreateAsync(users: 6, stock: 5, ct: Ct);
        Guid coupon = await Coupon(f, 50, 5, f.Users[0]);
        await using (var edit = await f.Sessions.BeginAsync(Ct))
        {
            await edit.ExecuteAsync(new DbStatement("UPDATE shop.products SET description='public master description',icon_ref='public master icon',guidance='public master guidance' WHERE id=$1", DbValue.Uuid(f.Product)), Ct);
            await edit.ExecuteAsync(new DbStatement("UPDATE shop.brand_products SET title_mode='override',title_override='public brand A',description_mode='clear',icon_mode='override',icon_override='public A icon' WHERE brand_id=$1", DbValue.Uuid(f.Brands[0])), Ct);
            await edit.ExecuteAsync(new DbStatement("UPDATE shop.brand_products SET title_mode='override',title_override='private brand B',price_usdt=20,auto_price_toman=false,price_toman=777 WHERE brand_id=$1", DbValue.Uuid(f.Brands[1])), Ct);
            await edit.CommitAsync(Ct);
        }
        var catalog = Store(f);
        var a = await catalog.QuoteAsync(f.Context(0), CheckoutFixture.Actor(0), new(f.Product, 1, CheckoutPayment.Wallet), Ct);
        var b = await catalog.QuoteAsync(f.Context(1), CheckoutFixture.Actor(1), new(f.Product, 1, CheckoutPayment.Wallet), Ct);
        var other = await catalog.QuoteAsync(f.Context(3), CheckoutFixture.Actor(3), new(f.Product, 1, CheckoutPayment.Wallet), Ct);
        Assert.Equal(coupon, a.Terms.DiscountId);
        Assert.Null(b.Terms.DiscountId);
        Assert.Null(other.Terms.DiscountId);
        Assert.Equal(20m, b.TotalUsd.Amount);
        Assert.Equal("public brand A", a.Terms.Product.Title);
        Assert.Null(a.Terms.Product.Description);
        Assert.Equal("public A icon", a.Terms.Product.Icon);
        Assert.Equal("public master guidance", a.Terms.Product.Guidance);
        Assert.DoesNotContain("private brand B", a.Terms.ToString(), StringComparison.Ordinal);
        await NoMoney(f);
    }

    [Fact]
    public async Task PerMembershipLimitsApplyAlongsideTheSharedCouponCap()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 5, ct: Ct);
        var actor = await f.AdminAsync(Ct);
        await using (var issue = await f.Sessions.BeginAsync(Ct))
        {
            await new PricingStore(f.Sessions, f.Secrets).IssueAsync(issue, f.Context(0), actor,
                new(Guid.NewGuid(), "PUBLIC-PER-MEMBER", CouponKind.Promotion, 50, f.Product, MaximumUses: 5, MaximumPerMember: 1), Guid.NewGuid(), Ct);
            await issue.CommitAsync(Ct);
        }
        Assert.Equal(1, (await Assert.ThrowsAsync<DiscountQuantityException>(() => Store(f).QuoteAsync(f.Context(0), CheckoutFixture.Actor(0), new(f.Product, 2, CheckoutPayment.Wallet), Ct))).Maximum);
        await NoMoney(f);
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders", Ct));
    }

    [Fact]
    public async Task EditingPrepaidEligibilityCannotTransferDisplayOrQuoteOwnershipAwayFromThePaidSource()
    {
        await using var f = await CheckoutFixture.CreateAsync(users: 6, stock: 5, ct: Ct);
        var actor = await f.AdminAsync(Ct);
        var pricing = new PricingStore(f.Sessions, f.Secrets);
        Guid source = Guid.NewGuid(), coupon = Guid.NewGuid();
        await using (var issue = await f.Sessions.BeginAsync(Ct))
        {
            await issue.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.orders(id,user_id,product_id,quantity,order_kind,payment_status,currency,payment_method,unit_price_usdt,auto_price_toman_snapshot,
                    pricing_rounding_step_snapshot,price_calculation_version,payment_confirmed_at,brand_public_name_snapshot,product_title_snapshot,payment_terms_snapshot,
                    fulfillment_kind_snapshot,local_first_snapshot,is_preorder)
                VALUES ($1,$2,$3,5,'prepaid_units','paid','USDT','crypto',10,false,5000,$4,CURRENT_TIMESTAMP,'public brand','public product','public terms','local',true,false)
                """, DbValue.Uuid(source), DbValue.Uuid(f.Users[0]), DbValue.Uuid(f.Product), DbValue.Text(PricingPolicy.Version)), Ct);
            await pricing.IssueAsync(issue, f.Context(0), actor, new(coupon, "PUBLIC-PAID-OWNER", CouponKind.Prepaid, 100, f.Product, f.Users[0], source, 5), Guid.NewGuid(), Ct);
            await issue.CommitAsync(Ct);
        }
        var catalog = Store(f);
        Assert.Equal(coupon, (await catalog.QuoteAsync(f.Context(0), CheckoutFixture.Actor(0), new(f.Product, 1, CheckoutPayment.Wallet), Ct)).Terms.DiscountId);
        await using (var edit = await f.Sessions.BeginAsync(Ct))
        {
            Guid audit = Guid.NewGuid();
            await edit.ExecuteAsync(new DbStatement("UPDATE shop.discounts SET eligible_user_id=$1,revision=1 WHERE id=$2", DbValue.Uuid(f.Users[3]), DbValue.Uuid(coupon)), Ct);
            await edit.ExecuteAsync(new DbStatement("INSERT INTO shop.audit_events(id,actor_kind,actor_admin_id,brand_id,action,entity_kind,entity_id,reason,new_revision,correlation_id) VALUES ($1,'admin',$2,$3,'coupon_edit','discount',$4,'public owner edit fixture',1,gen_random_uuid())", DbValue.Uuid(audit), DbValue.Uuid(actor.AdminId), DbValue.Uuid(f.Brands[0]), DbValue.Uuid(coupon)), Ct);
            await edit.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.discount_revisions(id,discount_id,revision_no,audit_event_id,percent_off,max_total_uses,max_uses_per_user,usage_adjustment_units,
                    redeemed_count_at_revision,held_units_at_revision,starts_at,ends_at,active,product_id,eligible_user_id)
                SELECT gen_random_uuid(),id,revision,$1,percent_off,max_total_uses,max_uses_per_user,usage_adjustment_units,redeemed_count,0,starts_at,ends_at,active,product_id,eligible_user_id
                FROM shop.discounts WHERE id=$2
                """, DbValue.Uuid(audit), DbValue.Uuid(coupon)), Ct);
            await edit.CommitAsync(Ct);
        }
        Assert.Equal(CouponLookupOutcome.NotFound, (await pricing.LookupAsync(f.Context(3), CheckoutFixture.Actor(3), "PUBLIC-PAID-OWNER", Ct)).Outcome);
        await using (var read = await f.Sessions.BeginAsync(Ct))
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => pricing.ReadCodeAsync(read, f.Context(3), CheckoutFixture.Actor(3), coupon, Ct));
        Assert.Null((await catalog.QuoteAsync(f.Context(3), CheckoutFixture.Actor(3), new(f.Product, 1, CheckoutPayment.Wallet), Ct)).Terms.DiscountId);
        await NoMoney(f);
    }

    [Fact]
    public async Task PublishedSupplierOutageStaysVisibleAcrossBrandsAndHiddenListingsStayHidden()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await Supplier(f);
        int calls = 0;
        var catalog = Store(f, (_, _) => { calls++; return Task.FromResult(new SupplierCatalogResult(SupplierCatalogStatus.OutOfStock, 0, 2, "USD", "public quote")); });
        for (int i = 0; i < 3; i++)
        {
            var item = await catalog.GetAsync(f.Context(i), CheckoutFixture.Actor(i), f.Product, Ct);
            Assert.NotNull(item);
            Assert.Equal(CatalogAvailability.OutOfStock, item.Availability);
            Assert.Equal("public-checkout-" + i, item.Presentation.Brand);
        }
        await Coupon(f, 100, 10, f.Users[0]);
        await Assert.ThrowsAsync<PricingRejectedException>(() => catalog.QuoteAsync(f.Context(0), CheckoutFixture.Actor(0), new(f.Product, 1, CheckoutPayment.Wallet), Ct));
        await using (var hide = await f.Sessions.BeginAsync(Ct))
        {
            await hide.ExecuteAsync(new DbStatement("UPDATE shop.brand_products SET visible=false,revision=revision+1 WHERE brand_id=$1", DbValue.Uuid(f.Brands[0])), Ct);
            await hide.CommitAsync(Ct);
        }
        int before = calls;
        Assert.Null(await catalog.GetAsync(f.Context(0), CheckoutFixture.Actor(0), f.Product, Ct));
        Assert.Equal(before, calls);
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.brand_products WHERE visible", Ct));
        await NoMoney(f);
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders", Ct));
    }

    [Fact]
    public async Task LocalFirstCoverageBypassesTheSupplierAndMixedQuotesRefreshOnlyTheRemainder()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 3, ct: Ct);
        await Supplier(f);
        int calls = 0;
        var catalog = Store(f, (request, _) =>
        {
            calls++;
            Assert.Equal(2, request.Quantity);
            Assert.Equal("public-synthetic-account", request.AccountRef);
            return Task.FromResult(new SupplierCatalogResult(SupplierCatalogStatus.Available, 2, 7, "USD", "public source quote"));
        });
        var local = await catalog.PrepareAsync(f.Context(0), CheckoutFixture.Actor(0), new(f.Product, 3, CheckoutPayment.Wallet), Ct);
        Assert.Equal(new CheckoutSupply(3, 0, false, "local"), local.Supply);
        Assert.Equal(0, calls);
        var mixed = await catalog.PrepareAsync(f.Context(0), CheckoutFixture.Actor(0), new(f.Product, 5, CheckoutPayment.Wallet), Ct);
        Assert.Equal(new CheckoutSupply(3, 2, false, "supplier"), mixed.Supply);
        Assert.Equal(50m, mixed.Quote.TotalUsd.Amount);
        Assert.Equal(1, calls);
        await using (var consume = await f.Sessions.BeginAsync(Ct))
        {
            await catalog.RequireCurrentAsync(consume, f.Context(0), CheckoutFixture.Actor(0), mixed, Ct);
            await consume.CommitAsync(Ct);
        }
        await using var replay = await f.Sessions.BeginAsync(Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => catalog.RequireCurrentAsync(replay, f.Context(0), CheckoutFixture.Actor(0), mixed, Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.supplier_requests", Ct));
        await NoMoney(f);
    }

    [Fact]
    public async Task SupplierCostAndEligibilityRefreshNeverRepriceRetailOrReleaseRawErrors()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await Supplier(f);
        int calls = 0;
        var catalog = Store(f, (_, _) =>
        {
            calls++;
            return calls == 1 ? Task.FromResult(new SupplierCatalogResult(SupplierCatalogStatus.Available, 10, 999, "USD", "public expensive quote")) : throw new HttpRequestException("private provider error / account detail");
        });
        var quote = await catalog.QuoteAsync(f.Context(0), CheckoutFixture.Actor(0), new(f.Product, 2, CheckoutPayment.Wallet), Ct);
        Assert.Equal(20m, quote.TotalUsd.Amount);
        var item = await catalog.GetAsync(f.Context(0), CheckoutFixture.Actor(0), f.Product, Ct);
        Assert.NotNull(item);
        Assert.Equal(CatalogAvailability.OutOfStock, item.Availability);
        Assert.DoesNotContain("private provider", item.ToString(), StringComparison.Ordinal);
        Assert.Equal(20m, quote.TotalUsd.Amount);
        await NoMoney(f);
    }

    [Fact]
    public async Task SourceSwitchDuringTransportRejectsTheResultWithoutHoldingDatabaseLocks()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await Supplier(f);
        var catalog = Store(f, async (_, ct) =>
        {
            await using var change = await f.Sessions.BeginAsync(ct);
            Guid next = Guid.NewGuid();
            await change.ExecuteAsync(new DbStatement("INSERT INTO shop.product_supplier_sources(id,product_id,provider_code,external_product_id,enabled) VALUES ($1,$2,'synthetic','public next',true)", DbValue.Uuid(next), DbValue.Uuid(f.Product)), ct);
            await change.ExecuteAsync(new DbStatement("UPDATE shop.products SET active_supplier_source_id=$1,revision=revision+1 WHERE id=$2", DbValue.Uuid(next), DbValue.Uuid(f.Product)), ct);
            await change.CommitAsync(ct);
            return new(SupplierCatalogStatus.Available, 10, 1, "USD", "public old quote");
        });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => catalog.PrepareAsync(f.Context(0), CheckoutFixture.Actor(0), new(f.Product, 1, CheckoutPayment.Wallet), Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.product_supplier_sources WHERE observation_revision>0", Ct));
        await NoMoney(f);
    }

    [Fact]
    public async Task PreparedQuotesRejectForeignMembersChangedListingsAndChangedCouponCapacity()
    {
        await using var f = await CheckoutFixture.CreateAsync(users: 6, stock: 5, ct: Ct);
        var catalog = Store(f);
        var foreign = await catalog.PrepareAsync(f.Context(0), CheckoutFixture.Actor(0), new(f.Product, 1, CheckoutPayment.Wallet), Ct);
        await using (var wrong = await f.Sessions.BeginAsync(Ct))
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => catalog.RequireCurrentAsync(wrong, f.Context(1), CheckoutFixture.Actor(1), foreign, Ct));
        var changed = await catalog.PrepareAsync(f.Context(0), CheckoutFixture.Actor(0), new(f.Product, 1, CheckoutPayment.Wallet), Ct);
        await using (var edit = await f.Sessions.BeginAsync(Ct))
        {
            await edit.ExecuteAsync(new DbStatement("UPDATE shop.brand_products SET revision=revision+1,price_usdt=11 WHERE brand_id=$1", DbValue.Uuid(f.Brands[0])), Ct);
            await edit.CommitAsync(Ct);
        }
        await using (var stale = await f.Sessions.BeginAsync(Ct))
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => catalog.RequireCurrentAsync(stale, f.Context(0), CheckoutFixture.Actor(0), changed, Ct));
        Guid coupon = await Coupon(f, 10, 2, null);
        var capacity = await catalog.PrepareAsync(f.Context(0), CheckoutFixture.Actor(0), new(f.Product, 2, CheckoutPayment.Wallet), Ct);
        await using (var competitor = await f.Sessions.BeginAsync(Ct))
        {
            Guid order = Guid.NewGuid();
            await competitor.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.orders(id,user_id,product_id,quantity,order_kind,payment_status,currency,payment_method,unit_price_usdt,auto_price_toman_snapshot,
                    pricing_rounding_step_snapshot,price_calculation_version,discount_pct_snapshot,discount_units_snapshot,payment_due_at,brand_public_name_snapshot,
                    product_title_snapshot,payment_terms_snapshot,fulfillment_kind_snapshot,local_first_snapshot,is_preorder)
                VALUES ($1,$2,$3,1,'product_purchase','awaiting_payment','USDT','crypto',11,false,5000,$4,10,1,CURRENT_TIMESTAMP+INTERVAL '30 minutes','public brand','public product','public terms','local',true,false)
                """, DbValue.Uuid(order), DbValue.Uuid(f.Users[3]), DbValue.Uuid(f.Product), DbValue.Text(PricingPolicy.Version)), Ct);
            await new PricingStore(f.Sessions, f.Secrets).HoldAsync(competitor, f.Context(3), CheckoutFixture.Actor(3), coupon, order, Ct);
            await competitor.CommitAsync(Ct);
        }
        await using var consumed = await f.Sessions.BeginAsync(Ct);
        Assert.Equal(1, (await Assert.ThrowsAsync<DiscountQuantityException>(() => catalog.RequireCurrentAsync(consumed, f.Context(0), CheckoutFixture.Actor(0), capacity, Ct))).Maximum);
        await NoMoney(f);
    }

    [Fact]
    public async Task PaidFifoHeadProtectsLocalStockAndPreordersHaveNoInventedAvailabilityDate()
    {
        await using var f = await CheckoutFixture.CreateAsync(users: 6, stock: 10, ct: Ct);
        await using (var queue = await f.Sessions.BeginAsync(Ct))
        {
            await queue.ExecuteAsync(new DbStatement("UPDATE shop.products SET availability_policy='local_preorder',revision=revision+1 WHERE id=$1", DbValue.Uuid(f.Product)), Ct);
            await queue.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.orders(id,user_id,product_id,quantity,order_kind,payment_status,fulfillment_status,currency,payment_method,unit_price_usdt,
                    auto_price_toman_snapshot,pricing_rounding_step_snapshot,price_calculation_version,payment_confirmed_at,brand_public_name_snapshot,
                    product_title_snapshot,payment_terms_snapshot,fulfillment_kind_snapshot,local_first_snapshot,is_preorder)
                VALUES (gen_random_uuid(),$1,$2,20,'product_purchase','paid','waiting_stock','USDT','crypto',10,false,5000,$3,CURRENT_TIMESTAMP,'public brand','public item','public terms','local',true,true)
                """, DbValue.Uuid(f.Users[3]), DbValue.Uuid(f.Product), DbValue.Text(PricingPolicy.Version)), Ct);
            await queue.CommitAsync(Ct);
        }
        var prepared = await Store(f).PrepareAsync(f.Context(0), CheckoutFixture.Actor(0), new(f.Product, 2, CheckoutPayment.Wallet), Ct);
        Assert.Equal(new CheckoutSupply(0, 0, true, "local"), prepared.Supply);
        Assert.Equal(10L, await f.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='available'", Ct));
        await NoMoney(f);
    }

    [Fact]
    public async Task ExplicitRestockConsentQueuesEvaluationAndRecoveryCoalescesWorkWithoutMoney()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await Supplier(f);
        bool available = false;
        var catalog = Store(f, (_, _) => Task.FromResult(new SupplierCatalogResult(available ? SupplierCatalogStatus.Available : SupplierCatalogStatus.OutOfStock, available ? 10 : 0, 1, "USD", "public quote")));
        var observation = await catalog.ObserveRestockAsync(f.Context(0), CheckoutFixture.Actor(0), f.Product, Ct);
        Assert.True(observation.OutOfStock);
        await using (var subscribe = await f.Sessions.BeginAsync(Ct))
        {
            await StockSubscriptionStore.SubscribeAsync(subscribe, f.Context(0), CheckoutFixture.Actor(0), f.Users[0], observation, Ct);
            await subscribe.CommitAsync(Ct);
        }
        await using (var worker = await f.Sessions.BeginAsync(Ct))
        {
            var lease = await OutboxStore.ClaimAsync(worker, JobKind.EvaluateRestock, Guid.NewGuid(), TimeSpan.FromSeconds(30), Ct);
            Assert.NotNull(lease);
            await OutboxStore.CompleteAsync(worker, lease, Ct);
            await worker.CommitAsync(Ct);
        }
        available = true;
        Assert.Equal(CatalogAvailability.Available, (await catalog.GetAsync(f.Context(0), CheckoutFixture.Actor(0), f.Product, Ct))!.Availability);
        await catalog.GetAsync(f.Context(0), CheckoutFixture.Actor(0), f.Product, Ct);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='evaluate_restock' AND status='pending'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.product_stock_subscriptions WHERE closed_at IS NULL AND triggered_at IS NULL", Ct));
        Assert.Equal(3L, await f.CountAsync("SELECT count(*) FROM shop.brand_products WHERE visible", Ct));
        await NoMoney(f);
    }

    [Fact]
    public async Task BannedNeverStartedAndUnpublishedTargetsCauseNoSupplierCall()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await Supplier(f);
        var catalog = Store(f);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => catalog.GetAsync(f.Context(0), 99999, f.Product, Ct));
        await using (var block = await f.Sessions.BeginAsync(Ct))
        {
            await block.ExecuteAsync(new DbStatement("UPDATE shop.users SET banned_at=CURRENT_TIMESTAMP WHERE id=$1", DbValue.Uuid(f.Users[0])), Ct);
            await block.CommitAsync(Ct);
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => catalog.GetAsync(f.Context(0), CheckoutFixture.Actor(0), f.Product, Ct));
        await NoMoney(f);
    }

    private static async Task<Guid> Coupon(CheckoutFixture f, decimal percent, long cap, Guid? member)
    {
        var actor = await f.AdminAsync(Ct);
        Guid id = Guid.NewGuid();
        await using var session = await f.Sessions.BeginAsync(Ct);
        await new PricingStore(f.Sessions, f.Secrets).IssueAsync(session, f.Context(0), actor, new(id, "PUBLIC-" + id.ToString("N"), CouponKind.Promotion, percent, f.Product, member, MaximumUses: cap), Guid.NewGuid(), Ct);
        await session.CommitAsync(Ct);
        return id;
    }
    private static async Task Supplier(CheckoutFixture f)
    {
        await using var session = await f.Sessions.BeginAsync(Ct);
        Guid source = Guid.NewGuid();
        await session.ExecuteAsync(new DbStatement("INSERT INTO shop.product_supplier_sources(id,product_id,provider_code,external_product_id,external_variant_id,enabled) VALUES ($1,$2,'synthetic','public product ref','public variant',true)", DbValue.Uuid(source), DbValue.Uuid(f.Product)), Ct);
        await session.ExecuteAsync(new DbStatement("UPDATE shop.products SET fulfillment_kind='supplier',active_supplier_source_id=$1,revision=revision+1 WHERE id=$2", DbValue.Uuid(source), DbValue.Uuid(f.Product)), Ct);
        await session.CommitAsync(Ct);
    }
    private static async Task Bank(CheckoutFixture f)
    {
        await using var session = await f.Sessions.BeginAsync(Ct);
        await session.ExecuteAsync(new DbStatement("""
            UPDATE shop.brands SET market='iran',allow_bank=true,allow_crypto=true,policy_version=policy_version+1,bank_number_ciphertext=decode('01','hex'),
                bank_holder_ciphertext=decode('02','hex'),bank_key_id='current',bank_card_fingerprint=decode(repeat('55',32),'hex'),bank_changed_at=CURRENT_TIMESTAMP WHERE id=$1
            """, DbValue.Uuid(f.Brands[0])), Ct);
        await session.ExecuteAsync(new DbStatement("UPDATE shop.brand_products SET allow_bank=true,allow_crypto=true,auto_price_toman=false,price_toman=1000000,revision=revision+1 WHERE brand_id=$1", DbValue.Uuid(f.Brands[0])), Ct);
        await session.CommitAsync(Ct);
    }
    private static async Task NoMoney(CheckoutFixture f)
    {
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='allocated'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries", Ct));
    }
}
