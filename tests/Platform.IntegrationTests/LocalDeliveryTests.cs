using System.Text;
using Platform.Core;
using Platform.Fulfillment;
using Platform.Persistence;
using Platform.Security;
using Platform.Telegram;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class LocalDeliveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static DeliveryStore Store(CheckoutFixture f) => new(f.Sessions);
    private static WalletCheckoutService Checkout(CheckoutFixture f) => new(f.Sessions, f.Store,
        new CatalogStore(f.Sessions, [], (_, _) => throw new InvalidOperationException("Unexpected supplier transport.")));
    private static Task<ChannelMembership> Member(long chat, long actor, CancellationToken ct) => Task.FromResult(ChannelMembership.Member);
    private static async Task<Guid> Purchase(CheckoutFixture f, int qty = 1)
    {
        var request = new CheckoutRequest(OrderIds.New(), f.Product, qty);
        Assert.Equal(CheckoutOutcome.Purchased, (await Checkout(f).PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), request, Member, Ct)).Outcome);
        return request.OrderId;
    }

    [Fact]
    public async Task CompleteManifestKeepsCheckoutRedemptionAndEditsTheSavedPrivateOrderMessageOnce()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, stock: 3, ct: Ct);
        await Coupon(f, 3);
        Guid order = await Purchase(f, 3);
        await Anchor(f, order, 301);
        JobLease prepare = await Claim(f, JobKind.PrepareManifest);
        Guid? delivery = await Store(f).PrepareAsync(f.Context(0), prepare, Ct);
        Assert.NotNull(delivery);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='redeemed' AND units_covered=3", Ct));
        Assert.Equal(3L, await f.CountAsync("SELECT redeemed_count FROM shop.discounts", Ct));
        JobLease send = await Claim(f, JobKind.SendTelegram);
        int calls = 0;
        await new LocalDeliveryWorker(Store(f)).RunAsync(f.Context(0), send, (dispatch, _) =>
        {
            Assert.Equal(1001, dispatch.BotId);
            Assert.Equal(CheckoutFixture.Actor(0), dispatch.ChatId);
            Assert.Equal(301, dispatch.TargetMessageId);
            Assert.False(dispatch.IsFile);
            Assert.Contains("public synthetic credential", Encoding.UTF8.GetString(dispatch.Content.Span), StringComparison.Ordinal);
            Assert.DoesNotContain("public synthetic", dispatch.ToString(), StringComparison.Ordinal);
            calls++;
            return Task.FromResult(new DeliveryOutcome(DeliveryOutcomeKind.Succeeded, 301));
        }, Ct);
        await Store(f).RecordOutcomeAsync(f.Context(0), send, new(DeliveryOutcomeKind.Succeeded, 301), Ct);
        Assert.Equal(1, calls);
        Assert.Equal(3L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments WHERE status='released'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE fulfillment_status='completed'", Ct));
        byte[] manifest = await Store(f).ReadManifestAsync(f.Context(0), CheckoutFixture.Actor(0), order, Ct);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(manifest);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Store(f).ReadManifestAsync(f.Context(1), CheckoutFixture.Actor(1), order, Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE purpose='order_credentials'", Ct));
    }

    [Fact]
    public async Task MissingAnchorPreparesOneProtectedManifestButCreatesNoAutomaticNewMessage()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        Guid order = await Purchase(f);
        Assert.Null(await Store(f).PrepareAsync(f.Context(0), await Claim(f, JobKind.PrepareManifest), Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE release_authorized_at IS NOT NULL AND fulfillment_status='ready'", Ct));
        var admin = await f.AdminAsync(Ct);
        Guid command = Guid.NewGuid();
        Guid recovery = await Store(f).AuthorizeSendRecoveryAsync(f.Context(0), admin, order, command, Ct);
        Assert.Equal(recovery, await Store(f).AuthorizeSendRecoveryAsync(f.Context(0), admin, order, command, Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE purpose='order_credentials' AND action='send'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.audit_events WHERE action='delivery_recovery'", Ct));
    }

    [Fact]
    public async Task CrashAfterEditRecoversOnlyTheSameTargetAndManifestWithoutBusinessReplay()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        Guid order = await Purchase(f);
        await Anchor(f, order, 302);
        Guid delivery = (await Store(f).PrepareAsync(f.Context(0), await Claim(f, JobKind.PrepareManifest), Ct))!.Value;
        JobLease first = await Claim(f, JobKind.SendTelegram);
        Guid generation;
        using (var dispatch = await Store(f).BeginAsync(f.Context(0), first, Ct))
        { Assert.NotNull(dispatch); generation = dispatch.Generation; Assert.Equal(302, dispatch.TargetMessageId); }
        await Expire(f, first);
        await Store(f).RecoverExpiredAsync(f.Context(0), delivery, Ct);
        JobLease second = await Claim(f, JobKind.SendTelegram);
        await new LocalDeliveryWorker(Store(f)).RunAsync(f.Context(0), second, (dispatch, _) =>
        { Assert.Equal(generation, dispatch.Generation); Assert.Equal(302, dispatch.TargetMessageId); return Task.FromResult(new DeliveryOutcome(DeliveryOutcomeKind.Succeeded, 302)); }, Ct);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='allocated'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE purpose='order_credentials' AND status='succeeded'", Ct));
    }

    [Fact]
    public async Task UnknownNewMessagePausesUntilExplicitRecoveryAndLateKnownSuccessIsRetained()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        Guid order = await Purchase(f);
        await Store(f).PrepareAsync(f.Context(0), await Claim(f, JobKind.PrepareManifest), Ct);
        var admin = await f.AdminAsync(Ct);
        Guid delivery = await Store(f).AuthorizeSendRecoveryAsync(f.Context(0), admin, order, Guid.NewGuid(), Ct);
        JobLease lease = await Claim(f, JobKind.SendTelegram);
        await new LocalDeliveryWorker(Store(f)).RunAsync(f.Context(0), lease, (_, _) => throw new HttpRequestException("private transport details"), Ct);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE status='uncertain'", Ct));
        await using (var noRetry = await f.Sessions.BeginAsync(Ct)) Assert.Null(await OutboxStore.ClaimAsync(noRetry, JobKind.SendTelegram, Guid.NewGuid(), TimeSpan.FromSeconds(30), Ct));
        await Store(f).RecoverExpiredAsync(f.Context(0), delivery, Ct);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE status='uncertain'", Ct));
        await Store(f).RecordOutcomeAsync(f.Context(0), lease, new(DeliveryOutcomeKind.Succeeded, 450), Ct);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE status='succeeded' AND telegram_message_id=450", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE fulfillment_status='completed'", Ct));
    }

    [Fact]
    public async Task CrashAfterNewMessageDoesNotAutomaticallyReclaimItsExpiredLease()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        Guid order = await Purchase(f);
        await Store(f).PrepareAsync(f.Context(0), await Claim(f, JobKind.PrepareManifest), Ct);
        Guid delivery = await Store(f).AuthorizeSendRecoveryAsync(f.Context(0), await f.AdminAsync(Ct), order, Guid.NewGuid(), Ct);
        JobLease lease = await Claim(f, JobKind.SendTelegram);
        using (var dispatch = await Store(f).BeginAsync(f.Context(0), lease, Ct)) Assert.NotNull(dispatch);
        await Expire(f, lease);
        await Store(f).RecoverExpiredAsync(f.Context(0), delivery, Ct);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE status='uncertain'", Ct));
        await using var claim = await f.Sessions.BeginAsync(Ct);
        Assert.Null(await OutboxStore.ClaimAsync(claim, JobKind.SendTelegram, Guid.NewGuid(), TimeSpan.FromSeconds(30), Ct));
    }

    [Fact]
    public async Task UneditableAnchorNeedsAnAuditedNewIntentAndNeverFallsBackAutomatically()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        Guid order = await Purchase(f);
        await Anchor(f, order, 303);
        await Store(f).PrepareAsync(f.Context(0), await Claim(f, JobKind.PrepareManifest), Ct);
        JobLease edit = await Claim(f, JobKind.SendTelegram);
        await new LocalDeliveryWorker(Store(f)).RunAsync(f.Context(0), edit, (_, _) => Task.FromResult(new DeliveryOutcome(DeliveryOutcomeKind.Uneditable)), Ct);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE purpose='order_credentials' AND action='edit' AND status='failed'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE purpose='order_credentials' AND action='send'", Ct));
        await Store(f).AuthorizeSendRecoveryAsync(f.Context(0), await f.AdminAsync(Ct), order, Guid.NewGuid(), Ct);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE purpose='order_credentials' AND action='send' AND part_no=2", Ct));
    }

    [Fact]
    public async Task WrongTargetResultCannotCompleteTheOrderAndWrongCallbackCannotSaveAnAnchor()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        Guid order = await Purchase(f);
        await Anchor(f, order, 304);
        await using (var wrong = await f.Sessions.BeginAsync(Ct))
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => OrderDeliveryRouter.RecordAnchorAsync(wrong, Verified(f, 305), order, Ct));
        await Store(f).PrepareAsync(f.Context(0), await Claim(f, JobKind.PrepareManifest), Ct);
        await new LocalDeliveryWorker(Store(f)).RunAsync(f.Context(0), await Claim(f, JobKind.SendTelegram), (_, _) => Task.FromResult(new DeliveryOutcome(DeliveryOutcomeKind.Succeeded, 999)), Ct);
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE fulfillment_status='completed'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments WHERE status='released'", Ct));
    }

    [Fact]
    public async Task HundredUnitPaidEntitlementRedeemsOneNineAndTwentyAtCompleteLocalAllocation()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 1000, stock: 30, ct: Ct);
        var source = new CheckoutRequest(OrderIds.New(), f.Product, 100, CheckoutPayment.Wallet, CheckoutKind.PrepaidUnits);
        Assert.Equal(CheckoutOutcome.Purchased, (await Checkout(f).PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), source, Member, Ct)).Outcome);
        foreach (int qty in new[] { 1, 9, 20 })
        {
            Guid order = await Purchase(f, qty);
            Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='held'", Ct));
            await Store(f).PrepareAsync(f.Context(0), await Claim(f, JobKind.PrepareManifest), Ct);
        }
        Assert.Equal(30L, await f.CountAsync("SELECT redeemed_count FROM shop.discounts", Ct));
        Assert.Equal(70L, await f.CountAsync("SELECT max_total_uses-redeemed_count FROM shop.discounts", Ct));
        await f.AddStockAsync(71, Ct);
        var error = await Assert.ThrowsAsync<DiscountQuantityException>(() => Purchase(f, 71));
        Assert.Equal(70, error.Maximum);
        Assert.Equal(4L, await f.CountAsync("SELECT count(*) FROM shop.orders", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", Ct));
    }

    [Fact]
    public async Task ThreeOfFiveAcquiredUnitsCannotReleaseAnyContentOrRedeemTheWholeHold()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, stock: 3, ct: Ct);
        await Coupon(f, 5);
        await using (var source = await f.Sessions.BeginAsync(Ct))
        {
            Guid id = Guid.NewGuid();
            await source.ExecuteAsync(new DbStatement("INSERT INTO shop.product_supplier_sources(id,product_id,provider_code,external_product_id,enabled) VALUES ($1,$2,'synthetic','public product',true)", DbValue.Uuid(id), DbValue.Uuid(f.Product)), Ct);
            await source.ExecuteAsync(new DbStatement("UPDATE shop.products SET fulfillment_kind='supplier',active_supplier_source_id=$1,revision=revision+1 WHERE id=$2", DbValue.Uuid(id), DbValue.Uuid(f.Product)), Ct);
            await source.CommitAsync(Ct);
        }
        var catalog = new CatalogStore(f.Sessions, [new("synthetic", "public account")], (_, _) => Task.FromResult(new SupplierCatalogResult(SupplierCatalogStatus.Available, 2, 1, "USD", "public quote")));
        var request = new CheckoutRequest(OrderIds.New(), f.Product, 5);
        await new WalletCheckoutService(f.Sessions, f.Store, catalog).PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), request, Member, Ct);
        await using (var premature = await f.Sessions.BeginAsync(Ct))
        {
            await OutboxStore.EnqueueAsync(premature, JobIntent.Order(JobKind.PrepareManifest, f.Brands[0], request.OrderId), request.OrderId, DateTime.UtcNow, Ct);
            await premature.CommitAsync(Ct);
        }
        Assert.Null(await Store(f).PrepareAsync(f.Context(0), await Claim(f, JobKind.PrepareManifest), Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Store(f).ReadManifestAsync(f.Context(0), CheckoutFixture.Actor(0), request.OrderId, Ct));
        Guid coupon;
        await using (var read = await f.Sessions.BeginAsync(Ct)) coupon = await read.QuerySingleAsync(new DbStatement("SELECT discount_id FROM shop.discount_uses WHERE order_id=$1", DbValue.Uuid(request.OrderId)), row => row.GetGuid(0), Ct);
        await using (var early = await f.Sessions.BeginAsync(Ct))
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new PricingStore(f.Sessions).TransitionClaimAsync(early, f.Context(0), coupon, request.OrderId, true, Ct));
        await using (var direct = await f.Sessions.BeginAsync(Ct))
        {
            await direct.ExecuteAsync(new DbStatement("UPDATE shop.discount_uses SET status='redeemed' WHERE order_id=$1", DbValue.Uuid(request.OrderId)), Ct);
            await direct.ExecuteAsync(new DbStatement("UPDATE shop.discounts SET redeemed_count=redeemed_count+5 WHERE id=$1", DbValue.Uuid(coupon)), Ct);
            await Assert.ThrowsAsync<DbOperationException>(() => direct.CommitAsync(Ct));
        }
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='held' AND units_covered=5", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT redeemed_count FROM shop.discounts", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE release_authorized_at IS NOT NULL", Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PaidSupplierShortfallRecoversOnlyTheVerifiedWholeOrder(bool file)
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await SupplierFixture.ConfigureAsync(f, Ct);
        var admin = await f.AdminAsync(Ct);
        await using (var issue = await f.Sessions.BeginAsync(Ct))
        {
            await new PricingStore(f.Sessions).IssueAsync(issue, f.Context(0), admin,
                new(Guid.NewGuid(), "PUBLIC-SUPPLIER-SHORTFALL", CouponKind.Goodwill, 50, f.Product, f.Users[0], MaximumUses: 5), Guid.NewGuid(), Ct);
            await issue.CommitAsync(Ct);
        }

        long initialLedgerEntries = await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", Ct);
        Guid order = await SupplierFixture.AcceptAsync(f, 5, Ct);
        await Anchor(f, order, 307);
        await Anchor(f, order, 307);
        await using (var wrong = await f.Sessions.BeginAsync(Ct))
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => OrderDeliveryRouter.RecordAnchorAsync(wrong, Verified(f, 308), order, Ct));
        var port = new SyntheticSupplier { Submit = _ => throw new TimeoutException() };
        string credential = file ? new string('x', 900) : "public supplier credential";
        byte[] wireContent = Encoding.UTF8.GetBytes(credential);
        SupplierOutcome Goods(SupplierBinding binding, int count) => new(SupplierOutcomeKind.Completed, "public purchase",
            binding.Currency, binding.Quantity, Enumerable.Range(0, count).Select(i => new SupplierItem("public item " + i,
                SupplierItemDisposition.Customer, "code", wireContent)).ToArray(), "public protected response"u8);

        var supplier = new SupplierRequestStore(f.Sessions);
        JobLease submit = await SupplierFixture.ClaimAsync(f, JobKind.SubmitSupplier, Ct);
        var binding = await supplier.ReadAsync(f.Context(0), submit, Ct);
        await new SupplierFulfillmentWorker(supplier, port, port).RunAsync(f.Context(0), submit, Ct);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.supplier_requests WHERE status='unknown'", Ct));

        port.Lookup = b => Goods(b, 3);
        await SupplierRecoveryTests.Due(f);
        JobLease lookup = await SupplierFixture.ClaimAsync(f, JobKind.ReconcileSupplier, Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new SupplierRequestStore(f.Sessions).ReadAsync(f.Context(1), lookup, Ct));
        await new SupplierFulfillmentWorker(new(f.Sessions), port, port).RunAsync(f.Context(0), lookup, Ct);
        foreach (JobKind kind in new[] { JobKind.ReconcileSupplier, JobKind.ExportSupplier, JobKind.ReconcileSupplier, JobKind.ExportSupplier })
        {
            await SupplierPartialQualification.ScheduleAsync(f, binding.RequestId, kind, Ct);
            await new SupplierFulfillmentWorker(new(f.Sessions), port, port).RunAsync(f.Context(0), await SupplierFixture.ClaimAsync(f, kind, Ct), Ct);
        }

        Assert.Equal(3L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments WHERE kind='supplier' AND status='acquired' AND delivered_content_key_id IS NOT NULL AND delivered_content <> convert_to('" + credential + "','UTF8') AND manifest_generation IS NULL", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.supplier_requests WHERE requested_quantity=5 AND status='partial_pending_admin'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE quantity=5 AND payment_status='paid' AND fulfillment_status='requires_admin' AND review_code='partial_acquisition' AND manifest_asset_ref IS NULL AND release_authorized_at IS NULL", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='held' AND units_covered=5", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT redeemed_count FROM shop.discounts", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE purpose='order_credentials'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='prepare_manifest'", Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Store(f).ReadManifestAsync(f.Context(0), CheckoutFixture.Actor(0), order, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Store(f).AuthorizeSendRecoveryAsync(f.Context(0), admin, order, Guid.NewGuid(), Ct));

        port.Lookup = b => Goods(b, 5);
        await SupplierPartialQualification.ScheduleAsync(f, binding.RequestId, JobKind.ExportSupplier, Ct);
        await new SupplierFulfillmentWorker(new(f.Sessions), port, port).RunAsync(f.Context(0), await SupplierFixture.ClaimAsync(f, JobKind.ExportSupplier, Ct), Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Store(f).ReadManifestAsync(f.Context(0), CheckoutFixture.Actor(0), order, Ct));
        long revision = await SupplierPartialQualification.RevisionAsync(f, order, Ct);
        Guid approval = Guid.NewGuid();
        await supplier.ApproveCompleteAsync(admin, f.Context(0), order, binding.RequestId, revision, approval, "Public supplier shortfall review", Ct);
        await supplier.ApproveCompleteAsync(admin, f.Context(0), order, binding.RequestId, revision, approval, "Public supplier shortfall review", Ct);
        await Store(f).PrepareAsync(f.Context(0), await Claim(f, JobKind.PrepareManifest), Ct);
        byte[] manifest = await Store(f).ReadManifestAsync(f.Context(0), CheckoutFixture.Actor(0), order, Ct);
        using var manifestStream = new MemoryStream(manifest);
        using var reader = new BinaryReader(manifestStream);
        Assert.Equal(1, reader.ReadInt32());
        Assert.Equal(5, reader.ReadInt32());
        for (int unit = 1; unit <= 5; unit++)
        {
            Assert.Equal(unit, reader.ReadInt32());
            Assert.Equal("code", reader.ReadString());
            Assert.Equal(credential, Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32())));
        }
        Assert.Equal(manifestStream.Length, manifestStream.Position);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Store(f).ReadManifestAsync(f.Context(1), CheckoutFixture.Actor(0), order, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Store(f).ReadManifestAsync(f.Context(0), CheckoutFixture.Actor(3), order, Ct));

        if (file)
        {
            Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE purpose='order_credentials'", Ct));
            Guid recovery = Guid.NewGuid();
            await Store(f).AuthorizeSendRecoveryAsync(f.Context(0), admin, order, recovery, Ct);
            JobLease send = await Claim(f, JobKind.SendTelegram);
            await new LocalDeliveryWorker(Store(f)).RunAsync(f.Context(0), send, (dispatch, _) =>
            {
                Assert.True(dispatch.IsFile);
                Assert.True(dispatch.Content.Span.SequenceEqual(manifest));
                return Task.FromResult(new DeliveryOutcome(DeliveryOutcomeKind.Unknown));
            }, Ct);
            await using (var noRetry = await f.Sessions.BeginAsync(Ct))
                Assert.Null(await OutboxStore.ClaimAsync(noRetry, JobKind.SendTelegram, Guid.NewGuid(), TimeSpan.FromSeconds(30), Ct));
            await Store(f).RecordOutcomeAsync(f.Context(0), send, new(DeliveryOutcomeKind.Succeeded, 308), Ct);
        }
        else
        {
            JobLease edit = await Claim(f, JobKind.SendTelegram);
            Guid delivery;
            Guid generation;
            byte[] content;
            using (var dispatch = await Store(f).BeginAsync(f.Context(0), edit, Ct))
            {
                Assert.NotNull(dispatch);
                Assert.Equal(307, dispatch.TargetMessageId);
                delivery = dispatch.Id;
                generation = dispatch.Generation;
                content = dispatch.Content.ToArray();
            }
            await Expire(f, edit);
            await Store(f).RecoverExpiredAsync(f.Context(0), delivery, Ct);
            await new LocalDeliveryWorker(Store(f)).RunAsync(f.Context(0), await Claim(f, JobKind.SendTelegram), (dispatch, _) =>
            {
                Assert.Equal(generation, dispatch.Generation);
                Assert.Equal(307, dispatch.TargetMessageId);
                Assert.Equal(CheckoutFixture.Actor(0), dispatch.ChatId);
                Assert.Equal(1001, dispatch.BotId);
                Assert.True(dispatch.Content.Span.SequenceEqual(content));
                return Task.FromResult(new DeliveryOutcome(DeliveryOutcomeKind.Succeeded, 307));
            }, Ct);
        }

        Assert.Equal(1, port.Submits);
        Assert.All(port.Intents, intent => Assert.Equal(binding.Intent, intent));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.supplier_request_attempts WHERE operation='submit'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit' AND signed_amount=-25", Ct));
        Assert.Equal(initialLedgerEntries + 1, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.audit_events WHERE action='manual_fulfillment'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='redeemed' AND units_covered=5", Ct));
        Assert.Equal(5L, await f.CountAsync("SELECT redeemed_count FROM shop.discounts", Ct));
        Assert.Equal(5L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments WHERE status='released'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(DISTINCT manifest_generation) FROM shop.fulfillments", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE purpose='order_credentials' AND status='succeeded'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE fulfillment_status='completed'", Ct));
    }

    [Fact]
    public async Task OversizeManifestRequiresExplicitFileRecoveryInsteadOfAnAutomaticEditOrSend()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        await using (var content = await f.Sessions.BeginAsync(Ct))
        {
            Guid id = await content.QuerySingleAsync(new DbStatement("SELECT id FROM shop.inventory_items"), r => r.GetGuid(0), Ct);
            byte[] encrypted = new InventoryCipher(f.Secrets).Encrypt(id, Encoding.UTF8.GetBytes(new string('x', 5000)));
            await content.ExecuteAsync(new DbStatement("UPDATE shop.inventory_items SET credential_ciphertext=$1 WHERE id=$2", DbValue.Binary(encrypted), DbValue.Uuid(id)), Ct);
            await content.CommitAsync(Ct);
        }
        Guid order = await Purchase(f);
        await Anchor(f, order, 306);
        Assert.Null(await Store(f).PrepareAsync(f.Context(0), await Claim(f, JobKind.PrepareManifest), Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE purpose='order_credentials'", Ct));
        await Store(f).AuthorizeSendRecoveryAsync(f.Context(0), await f.AdminAsync(Ct), order, Guid.NewGuid(), Ct);
        await new LocalDeliveryWorker(Store(f)).RunAsync(f.Context(0), await Claim(f, JobKind.SendTelegram), (dispatch, _) =>
        { Assert.True(dispatch.IsFile); Assert.Null(dispatch.TargetMessageId); Assert.True(dispatch.Content.Length > 5000); return Task.FromResult(new DeliveryOutcome(DeliveryOutcomeKind.Succeeded, 501)); }, Ct);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE fulfillment_status='completed'", Ct));
    }

    [Fact]
    public async Task OversizePreparationPersistsReviewWithoutReleaseOrAnAutomaticRetryLoop()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, stock: 17, ct: Ct);
        await Coupon(f, 17);
        byte[] plain = new byte[1048576];
        Array.Fill(plain, (byte)'x');
        await using (var content = await f.Sessions.BeginAsync(Ct))
        {
            var ids = await content.QueryManyAsync(new DbStatement("SELECT id FROM shop.inventory_items"), r => r.GetGuid(0), 1000, Ct);
            foreach (Guid id in ids)
                await content.ExecuteAsync(new DbStatement("UPDATE shop.inventory_items SET credential_ciphertext=$1 WHERE id=$2", DbValue.Binary(new InventoryCipher(f.Secrets).Encrypt(id, plain)), DbValue.Uuid(id)), Ct);
            await content.CommitAsync(Ct);
        }
        Guid order = await Purchase(f, 17);
        Assert.Null(await Store(f).PrepareAsync(f.Context(0), await Claim(f, JobKind.PrepareManifest), Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE fulfillment_status='requires_admin' AND review_code='credential_recovery' AND release_authorized_at IS NULL", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='prepare_manifest' AND status='failed' AND last_error_code='credential_recovery'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='redeemed' AND units_covered=17", Ct));
        Assert.Equal(17L, await f.CountAsync("SELECT redeemed_count FROM shop.discounts", Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Store(f).ReadManifestAsync(f.Context(0), CheckoutFixture.Actor(0), order, Ct));
    }

    [Fact]
    public async Task LateKnownEditSuccessSuppressesAnAuthorizedRecoveryBeforeANewSend()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        Guid order = await Purchase(f);
        await Anchor(f, order, 307);
        await Store(f).PrepareAsync(f.Context(0), await Claim(f, JobKind.PrepareManifest), Ct);
        JobLease original = await Claim(f, JobKind.SendTelegram);
        await new LocalDeliveryWorker(Store(f)).RunAsync(f.Context(0), original, (_, _) => Task.FromResult(new DeliveryOutcome(DeliveryOutcomeKind.Uneditable)), Ct);
        await Store(f).AuthorizeSendRecoveryAsync(f.Context(0), await f.AdminAsync(Ct), order, Guid.NewGuid(), Ct);
        await Store(f).RecordOutcomeAsync(f.Context(0), original, new(DeliveryOutcomeKind.Succeeded, 307), Ct);
        int sends = 0;
        await new LocalDeliveryWorker(Store(f)).RunAsync(f.Context(0), await Claim(f, JobKind.SendTelegram), (_, _) =>
        { sends++; return Task.FromResult(new DeliveryOutcome(DeliveryOutcomeKind.Succeeded, 999)); }, Ct);
        Assert.Equal(0, sends);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE purpose='order_credentials' AND status='succeeded'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE purpose='order_credentials' AND status='cancelled'", Ct));
    }

    private static async Task<JobLease> Claim(CheckoutFixture f, JobKind kind)
    {
        await using var session = await f.Sessions.BeginAsync(Ct);
        JobLease? lease = await OutboxStore.ClaimAsync(session, kind, Guid.NewGuid(), TimeSpan.FromSeconds(30), Ct);
        Assert.NotNull(lease);
        await session.CommitAsync(Ct);
        return lease;
    }
    private static async Task Expire(CheckoutFixture f, JobLease lease)
    {
        await using var session = await f.Sessions.BeginAsync(Ct);
        await session.ExecuteAsync(new DbStatement("UPDATE shop.jobs SET lease_until=clock_timestamp()-INTERVAL '1 second' WHERE id=$1", DbValue.Uuid(lease.Id)), Ct);
        await session.CommitAsync(Ct);
    }
    private static async Task Anchor(CheckoutFixture f, Guid order, long message)
    {
        await using var session = await f.Sessions.BeginAsync(Ct);
        await session.ExecuteAsync(new DbStatement("UPDATE shop.users SET flow_state='payment',flow_order_id=$1,flow_product_id=$2,flow_anchor_message_id=$3,flow_expires_at=clock_timestamp()+INTERVAL '1 hour',flow_revision=flow_revision+1 WHERE id=$4",
            DbValue.Uuid(order), DbValue.Uuid(f.Product), DbValue.Bigint(message), DbValue.Uuid(f.Users[0])), Ct);
        await OrderDeliveryRouter.RecordAnchorAsync(session, Verified(f, message), order, Ct);
        await session.CommitAsync(Ct);
    }
    private static VerifiedTelegramUpdate Verified(CheckoutFixture f, long message)
    {
        byte[] secret = Enumerable.Repeat((byte)7, 32).ToArray();
        using var verifier = new TelegramWebhookVerifier(f.Context(0), secret);
        string header = Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string body = "{\"update_id\":1,\"callback_query\":{\"from\":{\"id\":7001,\"is_bot\":false},\"data\":\"order\",\"message\":{\"message_id\":" + message + ",\"from\":{\"id\":1001,\"is_bot\":true},\"chat\":{\"id\":7001,\"type\":\"private\"}}}}";
        return verifier.Verify([header], Encoding.UTF8.GetBytes(body));
    }
    private static async Task Coupon(CheckoutFixture f, int cap)
    {
        var admin = await f.AdminAsync(Ct);
        await using var session = await f.Sessions.BeginAsync(Ct);
        await new PricingStore(f.Sessions).IssueAsync(session, f.Context(0), admin, new(Guid.NewGuid(), "PUBLIC-FREE", CouponKind.Goodwill, 100, f.Product, f.Users[0], MaximumUses: cap), Guid.NewGuid(), Ct);
        await session.CommitAsync(Ct);
    }
}
