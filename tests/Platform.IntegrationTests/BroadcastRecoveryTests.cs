using System.Security.Cryptography;
using System.Text;
using Platform.Campaigns;
using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class BroadcastRecoveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly IReadOnlyDictionary<string, string> Accounts = new Dictionary<string, string>();
    [Fact]
    public async Task CampaignVariantsMaterializeOnePrivateRecipientPerBrandBatchAndKnownSuccessIsSuppressed()
    {
        await using var f = await CheckoutFixture.CreateAsync(users: 6, ct: Ct);
        var actor = await f.AdminAsync(Ct);
        await using (var edit = await f.Sessions.BeginAsync(Ct))
        {
            await edit.ExecuteAsync(new DbStatement("UPDATE shop.users SET language='fa' WHERE id=$1", DbValue.Uuid(f.Users[0])), Ct);
            await edit.ExecuteAsync(new DbStatement("UPDATE shop.users SET marketing_opt_out_at=clock_timestamp() WHERE id=$1", DbValue.Uuid(f.Users[4])), Ct);
            await edit.CommitAsync(Ct);
        }
        var store = new CampaignStore(f.Sessions);
        Guid batch = Guid.NewGuid();
        CampaignDraft[] variants = [new(Guid.NewGuid(), f.Brands[0], "en", "all", null, null, "Public A", null), new(Guid.NewGuid(), f.Brands[0], "fa", "all", null, null, "Public A fa", null),
            new(Guid.NewGuid(), f.Brands[1], "en", "all", null, null, "Public B", null), new(Guid.NewGuid(), f.Brands[2], "en", "all", null, null, "Public C", null)];
        await store.CreateAsync(actor, batch, variants, Ct);
        await store.CreateAsync(actor, batch, variants.Reverse().ToArray(), Ct);
        await Assert.ThrowsAsync<DbOperationException>(() => store.CreateAsync(actor, batch, variants.Select(v => v with { Text = "Changed input" }).ToArray(), Ct));
        Guid send = Guid.NewGuid();
        await store.TransitionAsync(actor, batch, false, send, "Public launch", Ct);
        await store.TransitionAsync(actor, batch, false, send, "Public launch", Ct);
        await MaterializeAll(f, store);
        Assert.Equal(5L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE purpose='campaign'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM (SELECT campaign_batch_id,user_id FROM shop.telegram_deliveries GROUP BY campaign_batch_id,user_id HAVING count(*)>1) duplicates", Ct));
        int calls = 0;
        while (await store.ClaimNoticeAsync(Guid.NewGuid(), Ct) is JobLease lease)
        {
            var context = Context(f, lease);
            await new BroadcastWorker(store, TimeSpan.FromMilliseconds(100), null, Accounts).RunAsync(context, lease, (notice, text, _) =>
            {
                calls++;
                Assert.Equal("customer_dm", notice.Recipient);
                Assert.True(notice.ChatId is 7001 or 7002);
                Assert.DoesNotContain("campaign_batch", text!, StringComparison.Ordinal);
                return Task.FromResult(new NoticeOutcome(NoticeOutcomeKind.Sent, 1000 + calls));
            }, Ct);
            await Due(f);
        }
        Assert.Equal(5, calls);
        Assert.Equal(5L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE status='succeeded'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE user_id='" + f.Users[4].ToString("D") + "'", Ct));
    }
    [Fact]
    public async Task CrashBeforeDispatchCanRecoverTheSameIntentWithoutWeakeningLaterAttemptFences()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        var store = await Launch(f);
        var old = (await store.ClaimNoticeAsync(Guid.NewGuid(), Ct))!;
        await using (var expire = await f.Sessions.BeginAsync(Ct))
        { await expire.ExecuteAsync(new DbStatement("UPDATE shop.jobs SET lease_until=clock_timestamp()-INTERVAL '1 second' WHERE id=$1", DbValue.Uuid(old.Id)), Ct); await expire.CommitAsync(Ct); }
        Assert.Equal(1, await store.RecoverExpiredNoticesAsync(Ct));
        var current = (await store.ClaimNoticeAsync(Guid.NewGuid(), Ct))!;
        Assert.Equal(old.Id, current.Id);
        Assert.Equal(old.Attempt + 1, current.Attempt);
        int calls = 0;
        await new BroadcastWorker(store, TimeSpan.FromMilliseconds(100), null, Accounts).RunAsync(f.Context(0), current, (_, _, _) =>
        { calls++; return Task.FromResult(new NoticeOutcome(NoticeOutcomeKind.Sent, 444)); }, Ct);
        var error = await Assert.ThrowsAsync<DbOperationException>(() => store.BeginNoticeAsync(f.Context(0), old, TimeSpan.FromMilliseconds(100), null, Accounts, Ct));
        Assert.Equal(DbFailureKind.LeaseLost, error.Failure.Kind);
        Assert.Equal(1, calls);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE status='succeeded'", Ct));
    }
    [Fact]
    public async Task CrashAfterDispatchPausesUnknownSendAndLateKnownReceiptRemainsRecordable()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        var store = await Launch(f);
        var lease = (await store.ClaimNoticeAsync(Guid.NewGuid(), Ct))!;
        var notice = await store.BeginNoticeAsync(f.Context(0), lease, TimeSpan.FromMilliseconds(100), null, Accounts, Ct);
        Assert.NotNull(notice);
        await using (var expire = await f.Sessions.BeginAsync(Ct))
        { await expire.ExecuteAsync(new DbStatement("UPDATE shop.jobs SET lease_until=clock_timestamp()-INTERVAL '1 second' WHERE id=$1", DbValue.Uuid(lease.Id)), Ct); await expire.CommitAsync(Ct); }
        Assert.Equal(1, await store.RecoverExpiredNoticesAsync(Ct));
        Assert.Null(await store.ClaimNoticeAsync(Guid.NewGuid(), Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE status='uncertain'", Ct));
        await store.RecordNoticeAsync(f.Context(0), lease, new(NoticeOutcomeKind.Sent, 9999), Ct);
        await store.RecordNoticeAsync(f.Context(0), lease, new(NoticeOutcomeKind.Sent, 9999), Ct);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE status='succeeded' AND telegram_message_id=9999", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.campaigns WHERE status='completed'", Ct));
    }
    [Fact]
    public async Task Known429BacksOffTheBotWithoutCreatingAnotherLogicalRecipient()
    {
        await using var f = await CheckoutFixture.CreateAsync(users: 6, ct: Ct);
        var store = await Launch(f);
        var lease = (await store.ClaimNoticeAsync(Guid.NewGuid(), Ct))!;
        var worker = new BroadcastWorker(store, TimeSpan.FromMilliseconds(100), null, Accounts);
        int calls = 0;
        await worker.RunAsync(f.Context(0), lease, (_, _, _) => { calls++; return Task.FromResult(new NoticeOutcome(NoticeOutcomeKind.RateLimited, RetryAfterSeconds: 2)); }, Ct);
        var next = (await store.ClaimNoticeAsync(Guid.NewGuid(), Ct))!;
        await worker.RunAsync(f.Context(0), next, (_, _, _) => { calls++; return Task.FromResult(new NoticeOutcome(NoticeOutcomeKind.Sent, 777)); }, Ct);
        Assert.Equal(1, calls);
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE status='retry'", Ct));
        await Due(f);
        while (await store.ClaimNoticeAsync(Guid.NewGuid(), Ct) is JobLease retry)
        {
            await worker.RunAsync(f.Context(0), retry, (_, _, _) => Task.FromResult(new NoticeOutcome(NoticeOutcomeKind.Sent, 100 + retry.Attempt)), Ct);
            await Due(f);
        }
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE status='succeeded'", Ct));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndBlockChangesNeverCreateExtraOrRetargetedNotifications(bool dispatched)
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        var store = await Launch(f);
        var actor = await f.AdminAsync(Ct);
        Guid batch;
        await using (var read = await f.Sessions.BeginAsync(Ct)) batch = await read.QuerySingleAsync(new DbStatement("SELECT batch_id FROM shop.campaigns"), r => r.GetGuid(0), Ct);
        var lease = (await store.ClaimNoticeAsync(Guid.NewGuid(), Ct))!;
        if (dispatched) Assert.NotNull(await store.BeginNoticeAsync(f.Context(0), lease, TimeSpan.FromMilliseconds(100), null, Accounts, Ct));
        Guid cancel = Guid.NewGuid();
        await store.TransitionAsync(actor, batch, true, cancel, "Public cancellation", Ct);
        await store.TransitionAsync(actor, batch, true, cancel, "Public cancellation", Ct);
        if (dispatched) await store.RecordNoticeAsync(f.Context(0), lease, new(NoticeOutcomeKind.Sent, 555), Ct);
        else Assert.Null(await store.BeginNoticeAsync(f.Context(0), lease, TimeSpan.FromMilliseconds(100), null, Accounts, Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.campaigns WHERE status='cancelled'", Ct));
        Assert.Equal(dispatched ? 1L : 0L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE status='succeeded'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE status='leased'", Ct));
        Assert.Null(await store.ClaimNoticeAsync(Guid.NewGuid(), Ct));
    }
    [Fact]
    public async Task RestockConsentRechecksStockDefersSameAlertAndReoptInCreatesOnlyOneNewAlertWithoutCommerceEffects()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, stock: 0, ct: Ct);
        var store = new CampaignStore(f.Sessions);
        Guid consent = await Subscribe(f);
        await f.AddStockAsync(1, Ct);
        await Evaluate(f, store);
        await Evaluate(f, store);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE purpose='restock_notice'", Ct));
        await using (var retire = await f.Sessions.BeginAsync(Ct))
        { await retire.ExecuteAsync(new DbStatement("UPDATE shop.inventory_items SET state='retired'"), Ct); await retire.CommitAsync(Ct); }
        var lease = (await store.ClaimNoticeAsync(Guid.NewGuid(), Ct))!;
        int calls = 0;
        var worker = new BroadcastWorker(store, TimeSpan.FromMilliseconds(100), null, Accounts);
        await worker.RunAsync(f.Context(0), lease, (_, _, _) => { calls++; return Task.FromResult(new NoticeOutcome(NoticeOutcomeKind.Sent, 1)); }, Ct);
        Assert.Equal(0, calls);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.product_stock_subscriptions WHERE closed_at IS NULL AND triggered_at IS NOT NULL", Ct));
        await f.AddStockAsync(1, Ct);
        await Due(f);
        var retry = (await store.ClaimNoticeAsync(Guid.NewGuid(), Ct))!;
        await worker.RunAsync(f.Context(0), retry, (notice, text, _) =>
        { calls++; Assert.Contains("does not reserve stock", text!, StringComparison.Ordinal); Assert.Equal(CheckoutFixture.Actor(0), notice.ChatId); return Task.FromResult(new NoticeOutcome(NoticeOutcomeKind.Sent, 123)); }, Ct);
        Assert.Equal(1, calls);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.product_stock_subscriptions WHERE closed_at IS NOT NULL", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.payments", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.discounts", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='available'", Ct));
        await using (var retire = await f.Sessions.BeginAsync(Ct))
        { await retire.ExecuteAsync(new DbStatement("UPDATE shop.inventory_items SET state='retired' WHERE state='available'"), Ct); await retire.CommitAsync(Ct); }
        Guid again = await Subscribe(f);
        Assert.NotEqual(consent, again);
        await f.AddStockAsync(1, Ct);
        await Evaluate(f, store);
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE purpose='restock_notice'", Ct));
    }
    [Fact]
    public async Task UnsubscribeCancelsUnsentWorkWhileBlockedUsersAndUnknownSendsRemainSeparate()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        var store = new CampaignStore(f.Sessions);
        await Subscribe(f);
        await f.AddStockAsync(1, Ct);
        await Evaluate(f, store);
        await using (var unsubscribe = await f.Sessions.BeginAsync(Ct))
        { Assert.NotNull(await StockSubscriptionStore.UnsubscribeAsync(unsubscribe, f.Context(0), CheckoutFixture.Actor(0), f.Users[0], f.Product, Ct)); await unsubscribe.CommitAsync(Ct); }
        Assert.Null(await store.ClaimNoticeAsync(Guid.NewGuid(), Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE status='cancelled'", Ct));
        await Launch(f);
        await using (var block = await f.Sessions.BeginAsync(Ct))
        { await block.ExecuteAsync(new DbStatement("UPDATE shop.users SET blocked_at=clock_timestamp() WHERE id=$1", DbValue.Uuid(f.Users[0])), Ct); await block.CommitAsync(Ct); }
        var lease = (await store.ClaimNoticeAsync(Guid.NewGuid(), Ct))!;
        Assert.Null(await store.BeginNoticeAsync(f.Context(0), lease, TimeSpan.FromMilliseconds(100), null, Accounts, Ct));
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE status='cancelled'", Ct));
    }
    [Fact]
    public async Task VerifiedBonusMetadataUsesOnlyTrustedStaffDestinationAndNeverBecomesCustomerInventory()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, stock: 0, ct: Ct);
        var fixture = new PaymentSettlementFixture(f);
        await fixture.EnableAsync(Ct);
        await using (var source = await f.Sessions.BeginAsync(Ct))
        {
            Guid id = Guid.NewGuid();
            await source.ExecuteAsync(new DbStatement("INSERT INTO shop.product_supplier_sources(id,product_id,provider_code,external_product_id,enabled) VALUES ($1,$2,'synthetic','public product',true)", DbValue.Uuid(id), DbValue.Uuid(f.Product)), Ct);
            await source.ExecuteAsync(new DbStatement("UPDATE shop.products SET fulfillment_kind='supplier',active_supplier_source_id=$1,revision=revision+1", DbValue.Uuid(id)), Ct);
            await source.CommitAsync(Ct);
        }
        var order = await fixture.OrderAsync(ct: Ct);
        await fixture.CaptureAsync(order.Payment, order.Origin, order.Due, ct: Ct);
        await fixture.Store.SettleAsync(f.Context(0), order.Payment, PaymentSettlementFixture.Lookup, Ct);
        var actor = await f.AdminAsync(Ct);
        Guid request, audit = Guid.NewGuid(), bonus = Guid.NewGuid();
        string protectedValue = "Public synthetic administrator bonus credential";
        await using (var record = await f.Sessions.BeginAsync(Ct))
        {
            request = await record.QuerySingleAsync(new DbStatement("SELECT id FROM shop.supplier_requests"), r => r.GetGuid(0), Ct);
            byte[] bytes = Encoding.UTF8.GetBytes(protectedValue);
            try
            {
                await record.ExecuteAsync(new DbStatement("INSERT INTO shop.inventory_items(id,product_id,credential_content,source_fingerprint,origin_kind,supplier_request_id,state,remote_item_ref,supplier_item_key) VALUES ($1,$2,$3,$4,'supplier_bonus',$5,'admin_held','public bonus item',$6)",
                    DbValue.Uuid(bonus), DbValue.Uuid(f.Product), DbValue.Binary(bytes), DbValue.Binary(SHA256.HashData(bytes)), DbValue.Uuid(request), DbValue.Binary(SHA256.HashData(request.ToByteArray()))), Ct);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            await record.ExecuteAsync(new DbStatement("UPDATE shop.supplier_requests SET status='partial_pending_admin',review_code='partial_acquisition',evidence_ref='protected:synthetic/bonus-evidence',revision=revision+1 WHERE id=$1", DbValue.Uuid(request)), Ct);
            await AdminAuditStore.AppendAsync(record, actor, new(audit, AuditAction.Update, AuditEntity.SupplierRequest, request, f.Brands[0], AuditReason.OperatorReview, audit, SHA256.HashData("public verified bonus metadata"u8)), Ct);
            await record.CommitAsync(Ct);
        }
        var store = new CampaignStore(f.Sessions);
        Guid generation = Guid.NewGuid();
        Guid notice = await store.StaffNoticeAsync(actor, f.Context(0), request, audit, generation, true, -10088, Ct);
        Assert.Equal(notice, await store.StaffNoticeAsync(actor, f.Context(0), request, audit, generation, true, -10088, Ct));
        var lease = (await store.ClaimNoticeAsync(Guid.NewGuid(), Ct))!;
        var dispatch = await store.BeginNoticeAsync(f.Context(0), lease, TimeSpan.FromMilliseconds(100), -10088, Accounts, Ct);
        Assert.Equal("staff_chat", dispatch!.Recipient);
        Assert.Equal(-10088, dispatch.ChatId);
        Assert.DoesNotContain(protectedValue, dispatch.Text!, StringComparison.Ordinal);
        await store.RecordNoticeAsync(f.Context(0), lease, new(NoticeOutcomeKind.Sent, 876), Ct);
        await store.StaffNoticeAsync(actor, f.Context(0), request, audit, Guid.NewGuid(), true, -10099, Ct);
        var unapproved = (await store.ClaimNoticeAsync(Guid.NewGuid(), Ct))!;
        Assert.Null(await store.BeginNoticeAsync(f.Context(0), unapproved, TimeSpan.FromMilliseconds(100), -10088, Accounts, Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE recipient_kind='staff_chat' AND status='cancelled'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='admin_held'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='available'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments", Ct));
        await using var forged = await f.Sessions.BeginAsync(Ct);
        var error = await Assert.ThrowsAsync<DbOperationException>(() => forged.ExecuteAsync(new DbStatement("INSERT INTO shop.telegram_deliveries(id,brand_id,recipient_kind,user_id,purpose,action,dedupe_key,supplier_request_id,audit_event_id,delivery_generation) VALUES (gen_random_uuid(),$1,'customer_dm',$2,'staff_supplier_bonus','send','public-forged-staff-notice',$3,$4,$5)",
            DbValue.Uuid(f.Brands[0]), DbValue.Uuid(f.Users[0]), DbValue.Uuid(request), DbValue.Uuid(audit), DbValue.Uuid(generation)), Ct));
        Assert.Equal(DbFailureKind.Constraint, error.Failure.Kind);
    }
    [Fact]
    public async Task CleanupKeepsExact180DayBoundaryActiveConsentsAndEveryUnrelatedFinancialRecord()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        var store = new CampaignStore(f.Sessions);
        Guid old = Guid.NewGuid(), equal = Guid.NewGuid(), active = Guid.NewGuid();
        await using var session = await f.Sessions.BeginAsync(Ct);
        await session.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.product_stock_subscriptions(id,user_id,product_id,created_at,closed_at) VALUES
                ($1,$4,$5,CURRENT_TIMESTAMP-INTERVAL '200 days',CURRENT_TIMESTAMP-INTERVAL '4320 hours 1 second'),
                ($2,$4,$5,CURRENT_TIMESTAMP-INTERVAL '200 days',CURRENT_TIMESTAMP-INTERVAL '4320 hours'),
                ($3,$4,$5,CURRENT_TIMESTAMP-INTERVAL '200 days',NULL)
            """, DbValue.Uuid(old), DbValue.Uuid(equal), DbValue.Uuid(active), DbValue.Uuid(f.Users[0]), DbValue.Uuid(f.Product)), Ct);
        Guid delivery = Guid.NewGuid(), oldJob = Guid.NewGuid();
        await session.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.telegram_deliveries(id,brand_id,recipient_kind,user_id,purpose,action,dedupe_key,product_stock_subscription_id,status,sent_at,created_at)
            VALUES ($1,$2,'customer_dm',$3,'restock_notice','send','public-retained-restock',$4,'succeeded',CURRENT_TIMESTAMP-INTERVAL '4320 hours 1 second',CURRENT_TIMESTAMP-INTERVAL '200 days')
            """, DbValue.Uuid(delivery), DbValue.Uuid(f.Brands[0]), DbValue.Uuid(f.Users[0]), DbValue.Uuid(old)), Ct);
        await session.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.jobs(id,brand_id,job_kind,telegram_delivery_id,dedupe_key,status,created_at,completed_at)
            VALUES ($1,$2,'send_telegram',$3,'public-retained-job','succeeded',CURRENT_TIMESTAMP-INTERVAL '200 days',CURRENT_TIMESTAMP-INTERVAL '4320 hours 1 second')
            """, DbValue.Uuid(oldJob), DbValue.Uuid(f.Brands[0]), DbValue.Uuid(delivery)), Ct);
        Guid maintenance = Guid.NewGuid();
        await OutboxStore.EnqueueAsync(session, JobIntent.Maintenance(JobKind.SubscriptionCleanup), maintenance, DateTime.UtcNow.AddSeconds(-1), Ct);
        JobLease lease = (await OutboxStore.ClaimAsync(session, JobKind.SubscriptionCleanup, Guid.NewGuid(), TimeSpan.FromMinutes(1), Ct))!;
        Assert.Equal(1, await store.CleanupClosedSubscriptionsAsync(session, lease, Ct));
        Assert.Equal(2L, await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.product_stock_subscriptions"), r => r.GetInt64(0), Ct));
        Assert.Equal(3L, await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.wallet_entries"), r => r.GetInt64(0), Ct));
        Assert.Equal(0L, await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.telegram_deliveries"), r => r.GetInt64(0), Ct));
        Assert.Equal(1L, await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.jobs WHERE job_kind='subscription_cleanup'"), r => r.GetInt64(0), Ct));
        await session.CommitAsync(Ct);
    }
    internal static async Task<CampaignStore> Launch(CheckoutFixture f)
    {
        var store = new CampaignStore(f.Sessions);
        var actor = await f.AdminAsync(Ct);
        Guid batch = Guid.NewGuid();
        await store.CreateAsync(actor, batch, [new(Guid.NewGuid(), f.Brands[0], "en", "all", null, null, "Public campaign text", null)], Ct);
        await store.TransitionAsync(actor, batch, false, Guid.NewGuid(), "Public approval", Ct);
        await MaterializeAll(f, store);
        return store;
    }
    private static async Task MaterializeAll(CheckoutFixture f, CampaignStore store)
    {
        while (true)
        {
            JobLease? lease;
            await using (var claim = await f.Sessions.BeginAsync(Ct))
            { lease = await OutboxStore.ClaimAsync(claim, JobKind.CampaignMaterialize, Guid.NewGuid(), TimeSpan.FromMinutes(1), Ct); await claim.CommitAsync(Ct); }
            if (lease is null) break;
            await new AudienceMaterializer(store).RunAsync(Context(f, lease), lease, Ct);
        }
    }
    private static BrandContext Context(CheckoutFixture f, JobLease lease) => f.Contexts.Single(c => c.BrandId == lease.Intent.BrandId);
    private static async Task<Guid> Subscribe(CheckoutFixture f)
    {
        await using var session = await f.Sessions.BeginAsync(Ct);
        var observation = await session.QuerySingleAsync(new DbStatement("SELECT p.revision,b.revision,clock_timestamp() FROM shop.products p JOIN shop.brand_products b ON b.product_id=p.id WHERE p.id=$1 AND b.brand_id=$2", DbValue.Uuid(f.Product), DbValue.Uuid(f.Brands[0])), r => new RestockObservation(f.Product, r.GetInt64(0), r.GetInt64(1), r.GetUtc(2), true), Ct);
        Guid id = await StockSubscriptionStore.SubscribeAsync(session, f.Context(0), CheckoutFixture.Actor(0), f.Users[0], observation, Ct);
        await session.CommitAsync(Ct); return id;
    }
    private static async Task Evaluate(CheckoutFixture f, CampaignStore store)
    {
        JobLease lease;
        await using (var claim = await f.Sessions.BeginAsync(Ct))
        { await OutboxStore.EnqueueAsync(claim, JobIntent.Product(JobKind.EvaluateRestock, f.Product), Guid.NewGuid(), DateTime.UtcNow.AddSeconds(-1), Ct); lease = (await OutboxStore.ClaimAsync(claim, JobKind.EvaluateRestock, Guid.NewGuid(), TimeSpan.FromMinutes(1), Ct))!; await claim.CommitAsync(Ct); }
        await store.EvaluateRestockAsync(lease, Accounts, Ct);
    }
    private static async Task Due(CheckoutFixture f)
    {
        await using var session = await f.Sessions.BeginAsync(Ct);
        await session.ExecuteAsync(new DbStatement("UPDATE shop.jobs SET available_at=clock_timestamp()-INTERVAL '1 second' WHERE status='retry'"), Ct);
        await session.ExecuteAsync(new DbStatement("UPDATE shop.telegram_deliveries SET next_attempt_at=clock_timestamp()-INTERVAL '1 second',last_attempt_at=clock_timestamp()-INTERVAL '1 second' WHERE status IN ('retry','succeeded')"), Ct);
        await session.CommitAsync(Ct);
    }
}
