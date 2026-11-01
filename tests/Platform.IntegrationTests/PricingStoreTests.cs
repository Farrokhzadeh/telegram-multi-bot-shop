using Platform.Core;
using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class PricingStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly byte[] Market = "{\"status\":\"ok\",\"stats\":{\"usdt-rls\":{\"isClosed\":false,\"latest\":\"1234567\"}}}"u8.ToArray();

    [Fact]
    public async Task CouponCodeIsKeyedEncryptedScopedAndAuditedWithChangedInputReplayRejection()
    {
        await using var fixture = await CheckoutFixture.CreateAsync(users: 6, ct: Ct);
        var store = new PricingStore(fixture.Sessions, fixture.Secrets);
        var actor = await fixture.AdminAsync(Ct);
        var terms = new CouponTerms(Guid.NewGuid(), " public-coupon ", CouponKind.Goodwill, 50, fixture.Product, fixture.Users[0], MaximumUses: 5);
        Guid command = Guid.NewGuid();
        for (int i = 0; i < 2; i++)
        {
            await using var issue = await fixture.Sessions.BeginAsync(Ct);
            Assert.Equal(terms.Id, await store.IssueAsync(issue, fixture.Context(0), actor, terms, command, Ct));
            await issue.CommitAsync(Ct);
        }
        await using (var changed = await fixture.Sessions.BeginAsync(Ct))
            await Assert.ThrowsAsync<DbOperationException>(() => store.IssueAsync(changed, fixture.Context(0), actor, terms with { Percent = 10 }, command, Ct));
        Assert.Equal(1L, await fixture.CountAsync("SELECT count(*) FROM shop.discounts", Ct));
        Assert.Equal(1L, await fixture.CountAsync("SELECT count(*) FROM shop.discount_revisions", Ct));
        Assert.Equal(1L, await fixture.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='coupon_notice'", Ct));
        Assert.Equal(terms.Id, (await store.LookupAsync(fixture.Context(0), CheckoutFixture.Actor(0), "public-coupon", Ct)).DiscountId);
        Assert.Equal(CouponLookupOutcome.NotFound, (await store.LookupAsync(fixture.Context(1), CheckoutFixture.Actor(1), "public-coupon", Ct)).Outcome);
        Assert.Equal(CouponLookupOutcome.NotFound, (await store.LookupAsync(fixture.Context(3), CheckoutFixture.Actor(3), "public-coupon", Ct)).Outcome);
        await using var read = await fixture.Sessions.BeginAsync(Ct);
        Assert.Equal("PUBLIC-COUPON", await store.ReadCodeAsync(read, fixture.Context(0), CheckoutFixture.Actor(0), terms.Id, Ct));
        var code = await read.QuerySingleAsync(new DbStatement("SELECT code_digest,code_ciphertext FROM shop.discounts WHERE id=$1", DbValue.Uuid(terms.Id)), row => (Digest: row.GetBinary(0), Cipher: row.GetBinary(1)), Ct);
        Assert.Equal(32, code.Digest.Length);
        Assert.False(code.Cipher.AsSpan().SequenceEqual("PUBLIC-COUPON"u8));
        Assert.DoesNotContain("PUBLIC-COUPON", terms.ToString(), StringComparison.Ordinal);
        await using var foreign = await fixture.Sessions.BeginAsync(Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.ReadCodeAsync(foreign, fixture.Context(1), CheckoutFixture.Actor(1), terms.Id, Ct));
    }

    [Fact]
    public async Task GuessBudgetSurvivesStoreRestartAndIsIndependentPerBrandMembership()
    {
        await using var fixture = await CheckoutFixture.CreateAsync(ct: Ct);
        for (int i = 0; i < 20; i++)
            Assert.Equal(CouponLookupOutcome.NotFound, (await new PricingStore(fixture.Sessions, fixture.Secrets).LookupAsync(fixture.Context(0), CheckoutFixture.Actor(0), "PUBLIC-MISSING-" + i, Ct)).Outcome);
        Assert.Equal(CouponLookupOutcome.RateLimited, (await new PricingStore(fixture.Sessions, fixture.Secrets).LookupAsync(fixture.Context(0), CheckoutFixture.Actor(0), "PUBLIC-MISSING", Ct)).Outcome);
        Assert.Equal(CouponLookupOutcome.NotFound, (await new PricingStore(fixture.Sessions, fixture.Secrets).LookupAsync(fixture.Context(1), CheckoutFixture.Actor(1), "PUBLIC-MISSING", Ct)).Outcome);
        Assert.Equal(21L, await fixture.CountAsync("SELECT count(*) FROM shop.bot_activity_events WHERE idempotency_key LIKE 'coupon_lookup:%'", Ct));
        Assert.Equal(0L, await fixture.CountAsync("SELECT count(*) FROM shop.orders", Ct));
    }

    [Fact]
    public async Task ConcurrentWholeClaimsEnforceTheCouponCapWithoutReservingStockOrDebitingWallets()
    {
        await using var fixture = await CheckoutFixture.CreateAsync(users: 30, ct: Ct);
        var store = new PricingStore(fixture.Sessions, fixture.Secrets);
        var terms = new CouponTerms(Guid.NewGuid(), "PUBLIC-CAPPED", CouponKind.Promotion, 10, fixture.Product, MaximumUses: 3);
        await Issue(fixture, store, terms);
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(async i =>
        {
            int user = i * 3;
            await using var session = await fixture.Sessions.BeginAsync(Ct);
            try
            {
                Guid order = await Order(session, fixture, user, 2, terms.Percent);
                await store.HoldAsync(session, fixture.Context(user), CheckoutFixture.Actor(user), terms.Id, order, Ct);
                await session.CommitAsync(Ct);
                return true;
            }
            catch (DbOperationException) { return false; }
        }));
        Assert.Single(results, success => success);
        Assert.Equal(1L, await fixture.CountAsync("SELECT count(*) FROM shop.orders", Ct));
        Assert.Equal(2L, await fixture.CountAsync("SELECT sum(units_covered)::bigint FROM shop.discount_uses WHERE status='held'", Ct));
        Assert.Equal(0L, await fixture.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", Ct));
        Assert.Equal(0L, await fixture.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='allocated'", Ct));
    }

    [Fact]
    public async Task ExistingHoldKeepsItsGrantAndCancellationReleaseIsExactlyOnce()
    {
        await using var fixture = await CheckoutFixture.CreateAsync(ct: Ct);
        var store = new PricingStore(fixture.Sessions, fixture.Secrets);
        var terms = new CouponTerms(Guid.NewGuid(), "PUBLIC-HELD", CouponKind.Promotion, 50, fixture.Product, MaximumUses: 2);
        await Issue(fixture, store, terms);
        Guid order;
        CouponClaim first;
        await using (var hold = await fixture.Sessions.BeginAsync(Ct))
        {
            order = await Order(hold, fixture, 0, 2, terms.Percent);
            first = await store.HoldAsync(hold, fixture.Context(0), CheckoutFixture.Actor(0), terms.Id, order, Ct);
            Assert.Equal(10m, first.Saved.Amount);
            await hold.CommitAsync(Ct);
        }
        await using (var edit = await fixture.Sessions.BeginAsync(Ct))
        {
            Guid audit = Guid.NewGuid();
            var actor = await fixture.AdminAsync(Ct);
            await edit.ExecuteAsync(new DbStatement("UPDATE shop.discounts SET percent_off=10,max_total_uses=0,usage_adjustment_units=50,active=false,ends_at=CURRENT_TIMESTAMP-INTERVAL '1 day',revision=1 WHERE id=$1", DbValue.Uuid(terms.Id)), Ct);
            await edit.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.audit_events(id,actor_kind,actor_admin_id,brand_id,action,entity_kind,entity_id,reason,new_revision,correlation_id)
                VALUES ($1,'admin',$2,$3,'coupon_edit','discount',$4,'public owner edit fixture',1,gen_random_uuid())
                """, DbValue.Uuid(audit), DbValue.Uuid(actor.AdminId), DbValue.Uuid(fixture.Brands[0]), DbValue.Uuid(terms.Id)), Ct);
            await edit.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.discount_revisions(id,discount_id,revision_no,audit_event_id,percent_off,max_total_uses,max_uses_per_user,usage_adjustment_units,
                    redeemed_count_at_revision,held_units_at_revision,starts_at,ends_at,active,product_id,eligible_user_id)
                SELECT gen_random_uuid(),id,revision,$1,percent_off,max_total_uses,max_uses_per_user,usage_adjustment_units,redeemed_count,2,starts_at,ends_at,active,product_id,eligible_user_id
                FROM shop.discounts WHERE id=$2
                """, DbValue.Uuid(audit), DbValue.Uuid(terms.Id)), Ct);
            await edit.CommitAsync(Ct);
        }
        await using (var repeated = await fixture.Sessions.BeginAsync(Ct))
        {
            Assert.Equal(first, await store.HoldAsync(repeated, fixture.Context(0), CheckoutFixture.Actor(0), terms.Id, order, Ct));
            await repeated.CommitAsync(Ct);
        }
        await using (var cancel = await fixture.Sessions.BeginAsync(Ct))
        {
            await cancel.ExecuteAsync(new DbStatement("UPDATE shop.orders SET payment_status='cancelled',fulfillment_status='cancelled',resolution_kind='replaced',resolution_at=CURRENT_TIMESTAMP WHERE id=$1", DbValue.Uuid(order)), Ct);
            Assert.True(await store.TransitionClaimAsync(cancel, fixture.Context(0), terms.Id, order, false, Ct));
            Assert.False(await store.TransitionClaimAsync(cancel, fixture.Context(0), terms.Id, order, false, Ct));
            await cancel.CommitAsync(Ct);
        }
        Assert.Equal(1L, await fixture.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='released'", Ct));
        Assert.Equal(0L, await fixture.CountAsync("SELECT sum(redeemed_count)::bigint FROM shop.discounts", Ct));
        await using var resurrect = await fixture.Sessions.BeginAsync(Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.HoldAsync(resurrect, fixture.Context(0), CheckoutFixture.Actor(0), terms.Id, order, Ct));
    }

    [Fact]
    public async Task PrepaidSourceIssuesExactlyOneAccountBoundNUseCodeAndRejectsForeignMembership()
    {
        await using var fixture = await CheckoutFixture.CreateAsync(ct: Ct);
        var store = new PricingStore(fixture.Sessions, fixture.Secrets);
        Guid source;
        await using (var paid = await fixture.Sessions.BeginAsync(Ct))
        {
            source = await Order(paid, fixture, 0, 5, null, true);
            await paid.CommitAsync(Ct);
        }
        var terms = new CouponTerms(Guid.NewGuid(), "PUBLIC-PREPAID", CouponKind.Prepaid, 100, fixture.Product, fixture.Users[0], source, 5);
        await Issue(fixture, store, terms);
        Assert.Equal(1L, await fixture.CountAsync("SELECT count(*) FROM shop.discounts WHERE kind='prepaid_entitlement' AND max_total_uses=5", Ct));
        var actor = await fixture.AdminAsync(Ct);
        await using (var foreign = await fixture.Sessions.BeginAsync(Ct))
            await Assert.ThrowsAsync<DbOperationException>(() => store.IssueAsync(foreign, fixture.Context(0), actor, terms with { Id = Guid.NewGuid(), Code = "PUBLIC-FOREIGN", MemberId = fixture.Users[1] }, Guid.NewGuid(), Ct));
        await using var duplicate = await fixture.Sessions.BeginAsync(Ct);
        await Assert.ThrowsAsync<DbOperationException>(() => store.IssueAsync(duplicate, fixture.Context(0), actor, terms with { Id = Guid.NewGuid(), Code = "PUBLIC-DUPLICATE" }, Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task RedemptionRequiresCompleteOrderAndPostsTheRealUseCounterExactlyOnce()
    {
        await using var fixture = await CheckoutFixture.CreateAsync(ct: Ct);
        var store = new PricingStore(fixture.Sessions, fixture.Secrets);
        var terms = new CouponTerms(Guid.NewGuid(), "PUBLIC-REDEEM", CouponKind.Promotion, 50, fixture.Product, MaximumUses: 1);
        await Issue(fixture, store, terms);
        Guid order;
        await using (var hold = await fixture.Sessions.BeginAsync(Ct))
        {
            order = await Order(hold, fixture, 0, 1, terms.Percent);
            await store.HoldAsync(hold, fixture.Context(0), CheckoutFixture.Actor(0), terms.Id, order, Ct);
            await hold.CommitAsync(Ct);
        }
        await using (var early = await fixture.Sessions.BeginAsync(Ct))
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.TransitionClaimAsync(early, fixture.Context(0), terms.Id, order, true, Ct));
        await using (var complete = await fixture.Sessions.BeginAsync(Ct))
        {
            var stock = await complete.QuerySingleAsync(new DbStatement("SELECT id,credential_ciphertext,credential_key_id FROM shop.inventory_items"), row => (Id: row.GetGuid(0), Cipher: row.GetBinary(1), Key: row.GetString(2)), Ct);
            Guid unit = Guid.NewGuid(), generation = Guid.NewGuid();
            byte[] plain = new InventoryCipher(fixture.Secrets).Decrypt(stock.Id, stock.Key, stock.Cipher);
            byte[] content;
            try { content = new BrandCipher(fixture.Secrets).Encrypt(fixture.Context(0), "fulfillments", unit, "delivered_content", plain); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
            await complete.ExecuteAsync(new DbStatement("UPDATE shop.orders SET payment_status='paid',payment_confirmed_at=CURRENT_TIMESTAMP WHERE id=$1", DbValue.Uuid(order)), Ct);
            await complete.ExecuteAsync(new DbStatement("INSERT INTO shop.fulfillments(id,order_id,unit_no,kind,status,delivered_content,delivered_content_key_id,content_kind,manifest_generation) VALUES ($1,$2,1,'local','ready',$3,'current','text',$4)", DbValue.Uuid(unit), DbValue.Uuid(order), DbValue.Binary(content), DbValue.Uuid(generation)), Ct);
            await complete.ExecuteAsync(new DbStatement("UPDATE shop.inventory_items SET state='allocated',assigned_fulfillment_id=$1,allocated_at=CURRENT_TIMESTAMP WHERE id=$2", DbValue.Uuid(unit), DbValue.Uuid(stock.Id)), Ct);
            await complete.ExecuteAsync(new DbStatement("UPDATE shop.orders SET fulfillment_status='ready',delivery_generation=$1,manifest_asset_ref='public-synthetic-manifest',manifest_digest=decode(repeat('55',32),'hex'),manifest_prepared_at=CURRENT_TIMESTAMP,release_authorized_at=CURRENT_TIMESTAMP WHERE id=$2", DbValue.Uuid(generation), DbValue.Uuid(order)), Ct);
            Assert.True(await store.TransitionClaimAsync(complete, fixture.Context(0), terms.Id, order, true, Ct));
            Assert.False(await store.TransitionClaimAsync(complete, fixture.Context(0), terms.Id, order, true, Ct));
            await complete.CommitAsync(Ct);
        }
        Assert.Equal(1L, await fixture.CountAsync("SELECT sum(redeemed_count)::bigint FROM shop.discounts", Ct));
        Assert.Equal(1L, await fixture.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='redeemed'", Ct));
        await using var release = await fixture.Sessions.BeginAsync(Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.TransitionClaimAsync(release, fixture.Context(0), terms.Id, order, false, Ct));
    }

    [Fact]
    public async Task FxRefreshIsHourlyAndFencedAndPreservesManualTomanPrices()
    {
        await using var fixture = await CheckoutFixture.CreateAsync(ct: Ct);
        var store = new PricingStore(fixture.Sessions, fixture.Secrets);
        await EnableFx(fixture);
        Guid? job = await store.ScheduleFxAsync(fixture.Context(0), Ct);
        Assert.NotNull(job);
        Assert.Equal(job, await store.ScheduleFxAsync(fixture.Context(0), Ct));
        JobLease lease = await ClaimFx(fixture);
        await using (var apply = await fixture.Sessions.BeginAsync(Ct))
        {
            await store.ApplyFxAsync(apply, lease, 1, Market, Ct);
            await apply.CommitAsync(Ct);
        }
        Assert.Null(await store.ScheduleFxAsync(fixture.Context(0), Ct));
        await using (var stale = await fixture.Sessions.BeginAsync(Ct))
            await Assert.ThrowsAsync<DbOperationException>(() => store.ApplyFxAsync(stale, lease, 1, Market, Ct));
        await using var read = await fixture.Sessions.BeginAsync(Ct);
        Assert.Equal(("123456.7", PricingPolicy.FxSource, true, 2L), await read.QuerySingleAsync(new DbStatement("SELECT current_fx::text,fx_source,fx_error_code IS NULL,policy_version FROM shop.brands WHERE id=$1", DbValue.Uuid(fixture.Brands[0])), row => (row.GetString(0), row.GetString(1), row.GetBoolean(2), row.GetInt64(3)), Ct));
        Assert.Equal("12345", await read.QuerySingleAsync(new DbStatement("SELECT price_toman::text FROM shop.brand_products WHERE brand_id=$1 AND product_id=$2", DbValue.Uuid(fixture.Brands[0]), DbValue.Uuid(fixture.Product)), row => row.GetString(0), Ct));
    }

    [Fact]
    public async Task FxFailureRetainsTheLastGoodRateAndPolicyChangesFenceOldResponses()
    {
        await using var fixture = await CheckoutFixture.CreateAsync(ct: Ct);
        var store = new PricingStore(fixture.Sessions, fixture.Secrets);
        await EnableFx(fixture);
        await using (var old = await fixture.Sessions.BeginAsync(Ct))
        {
            await old.ExecuteAsync(new DbStatement("UPDATE shop.brands SET current_fx=120000,fx_source=$1,fx_fetched_at=CURRENT_TIMESTAMP-INTERVAL '2 hours' WHERE id=$2", DbValue.Text(PricingPolicy.FxSource), DbValue.Uuid(fixture.Brands[0])), Ct);
            await old.CommitAsync(Ct);
        }
        await store.ScheduleFxAsync(fixture.Context(0), Ct);
        JobLease lease = await ClaimFx(fixture);
        await using (var wrong = await fixture.Sessions.BeginAsync(Ct))
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.ApplyFxAsync(wrong, lease, 0, Market, Ct));
        await using (var fail = await fixture.Sessions.BeginAsync(Ct))
        {
            await store.FailFxAsync(fail, lease, 1, Ct);
            await fail.CommitAsync(Ct);
        }
        await using var read = await fixture.Sessions.BeginAsync(Ct);
        Assert.Equal(("120000", "fx_unavailable"), await read.QuerySingleAsync(new DbStatement("SELECT current_fx::text,fx_error_code FROM shop.brands WHERE id=$1", DbValue.Uuid(fixture.Brands[0])), row => (row.GetString(0), row.GetString(1)), Ct));
        Assert.Equal(1L, await fixture.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='refresh_fx' AND status='retry'", Ct));
    }

    private static async Task Issue(CheckoutFixture fixture, PricingStore store, CouponTerms terms)
    {
        var actor = await fixture.AdminAsync(Ct);
        await using var issue = await fixture.Sessions.BeginAsync(Ct);
        await store.IssueAsync(issue, fixture.Context(0), actor, terms, Guid.NewGuid(), Ct);
        await issue.CommitAsync(Ct);
    }
    private static async Task<Guid> Order(DbSession session, CheckoutFixture fixture, int user, int quantity, decimal? percent, bool prepaid = false)
    {
        Guid id = Guid.CreateVersion7();
        await session.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.orders(id,user_id,product_id,quantity,order_kind,payment_status,currency,payment_method,unit_price_usdt,
                auto_price_toman_snapshot,pricing_rounding_step_snapshot,price_calculation_version,brand_public_name_snapshot,product_title_snapshot,
                payment_terms_snapshot,fulfillment_kind_snapshot,local_first_snapshot,is_preorder,payment_due_at,payment_confirmed_at,discount_pct_snapshot,discount_units_snapshot)
            VALUES ($1,$2,$3,$4,$5,$6,'USDT','crypto',10,false,5000,$7,'public brand','public product','public terms','local',true,false,
                CASE WHEN $8 THEN NULL ELSE CURRENT_TIMESTAMP+INTERVAL '30 minutes' END,CASE WHEN $8 THEN CURRENT_TIMESTAMP ELSE NULL END,$9,$10)
            """, DbValue.Uuid(id), DbValue.Uuid(fixture.Users[user]), DbValue.Uuid(fixture.Product), DbValue.Integer(quantity), DbValue.Text(prepaid ? "prepaid_units" : "product_purchase"),
            DbValue.Text(prepaid ? "paid" : "awaiting_payment"), DbValue.Text(PricingPolicy.Version), DbValue.Boolean(prepaid), percent is decimal p ? DbValue.Numeric(p) : DbValue.Null(NpgsqlTypes.NpgsqlDbType.Numeric),
            percent is null ? DbValue.Null(NpgsqlTypes.NpgsqlDbType.Integer) : DbValue.Integer(quantity)), Ct);
        return id;
    }
    private static async Task EnableFx(CheckoutFixture fixture)
    {
        await using var enable = await fixture.Sessions.BeginAsync(Ct);
        await enable.ExecuteAsync(new DbStatement("UPDATE shop.brands SET fx_enabled=true,policy_version=1 WHERE id=$1", DbValue.Uuid(fixture.Brands[0])), Ct);
        await enable.ExecuteAsync(new DbStatement("UPDATE shop.brand_products SET auto_price_toman=false,price_toman=12345 WHERE brand_id=$1", DbValue.Uuid(fixture.Brands[0])), Ct);
        await enable.CommitAsync(Ct);
    }
    private static async Task<JobLease> ClaimFx(CheckoutFixture fixture)
    {
        await using var claim = await fixture.Sessions.BeginAsync(Ct);
        var lease = await OutboxStore.ClaimAsync(claim, JobKind.RefreshFx, Guid.NewGuid(), TimeSpan.FromSeconds(30), Ct);
        Assert.NotNull(lease);
        await claim.CommitAsync(Ct);
        return lease;
    }
}
