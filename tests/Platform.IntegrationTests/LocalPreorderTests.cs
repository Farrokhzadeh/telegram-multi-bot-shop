using System.Security.Cryptography;
using Npgsql;
using Platform.Core;
using Platform.Fulfillment;
using Platform.Persistence;
using Platform.Payments;
using Platform.Security;
using Platform.Telegram;
using Platform.Testing;

namespace Platform.IntegrationTests;

[Collection("PostgreSQL races")]
public sealed class LocalPreorderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task NativeCompatibleWholeFifoAndPrivateManifests() => await LocalPreorderQualification.RunAsync(Ct);
    [Fact]
    public async Task LostDrainCommitAcknowledgementRetainsWholeAllocationAndDurableContinuationAfterRestart() => await LocalPreorderQualification.RunAsync(Ct, true);
    [Theory]
    [InlineData(50)]
    [InlineData(100)]
    public async Task FiveThenTwoAcrossBrandsWaitWholeThroughThreeThreeOneAndReleaseOnlyCompleteManifests(int percent)
    {
        await using var f = await Fixture();
        var admin = await f.AdminAsync(Ct);
        Guid coupon = Guid.NewGuid();
        await using (var issue = await f.Sessions.BeginAsync(Ct))
        {
            await new PricingStore(f.Sessions).IssueAsync(issue, f.Context(0), admin, new(coupon, "PUBLIC-FIFO", CouponKind.Promotion, percent, f.Product, f.Users[0], MaximumUses: 5), Guid.NewGuid(), Ct);
            await issue.CommitAsync(Ct);
        }
        Guid a = await Purchase(f, 0, 5), b = await Purchase(f, 1, 2);
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE fulfillment_status='waiting_stock' AND payment_due_at IS NULL", Ct));
        await Restock(f, 3); await DrainAll(f);
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments", Ct));
        Assert.Equal(3L, await f.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='available'", Ct));
        await Restock(f, 3); await DrainAll(f);
        Assert.Equal(5L, await Units(f, a)); Assert.Equal(0L, await Units(f, b));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='held' AND units_covered=5", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE release_authorized_at IS NOT NULL", Ct));
        var delivery = new DeliveryStore(f.Sessions);
        Assert.Null(await delivery.PrepareAsync(f.Context(0), await Claim(f, JobKind.PrepareManifest), Ct));
        Assert.Equal(5L, await f.CountAsync("SELECT redeemed_count FROM shop.discounts", Ct));
        byte[] manifest = await delivery.ReadManifestAsync(f.Context(0), CheckoutFixture.Actor(0), a, Ct);
        Assert.NotEmpty(manifest); CryptographicOperations.ZeroMemory(manifest);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => delivery.ReadManifestAsync(f.Context(1), CheckoutFixture.Actor(1), a, Ct));
        await Restock(f, 1); await DrainAll(f);
        Assert.Equal(2L, await Units(f, b));
        await delivery.PrepareAsync(f.Context(1), await Claim(f, JobKind.PrepareManifest), Ct);
        await DrainAll(f);
        Assert.Equal(7L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments WHERE status='ready'", Ct));
        Assert.Equal(percent == 100 ? 1L : 2L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.supplier_requests", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='expire_order_payment'", Ct));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeldHeadBlocksUntilAuditedResumeOrCancellationWithoutChangingPriorityOrMoney(bool cancel)
    {
        await using var f = await Fixture();
        Guid a = await Purchase(f, 0, 5), b = await Purchase(f, 1, 2);
        DateTime accepted;
        await using (var read = await f.Sessions.BeginAsync(Ct)) accepted = await read.QuerySingleAsync(new DbStatement("SELECT payment_confirmed_at FROM shop.orders WHERE id=$1", DbValue.Uuid(a)), r => r.GetUtc(0), Ct);
        var admin = await f.AdminAsync(Ct);
        var store = new OrderAdministrationStore(f.Sessions);
        await store.ResolveAsync(admin, f.Context(0), a, new("pause_preorder", await Revision(f, a), "Public hold", Guid.NewGuid()), Ct);
        await Restock(f, 7); await DrainAll(f);
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments", Ct));
        await store.ResolveAsync(admin, f.Context(0), a, new(cancel ? "cancel_local_preorder" : "resume_preorder", await Revision(f, a), "Public resolution", Guid.NewGuid()), Ct);
        await DrainAll(f);
        Assert.Equal(cancel ? 0L : 5L, await Units(f, a)); Assert.Equal(2L, await Units(f, b));
        await using var verify = await f.Sessions.BeginAsync(Ct);
        Assert.Equal(accepted, await verify.QuerySingleAsync(new DbStatement("SELECT payment_confirmed_at FROM shop.orders WHERE id=$1", DbValue.Uuid(a)), r => r.GetUtc(0), Ct));
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE signed_amount>0 AND movement_kind<>'admin_adjustment'", Ct));
    }
    [Fact]
    public async Task LockedHeadTimesOutWithoutSkippingToSmallerOrderAndRecoversOnSameLease()
    {
        await using var f = await Fixture();
        Guid a = await Purchase(f, 0, 5), b = await Purchase(f, 1, 2);
        await Restock(f, 7);
        JobLease lease = await Claim(f);
        await using (var locked = await f.Sessions.BeginAsync(Ct))
        {
            await locked.QuerySingleAsync(new DbStatement("SELECT id FROM shop.orders WHERE id=$1 FOR UPDATE", DbValue.Uuid(a)), r => r.GetGuid(0), Ct);
            var sessions = new DbSessionFactory(f.Database.Data, new() { LockTimeout = TimeSpan.FromMilliseconds(100) });
            var worker = new LocalPreorderWorker(new(sessions), f.Contexts);
            var error = await Assert.ThrowsAsync<DbOperationException>(() => worker.RunAsync(lease, Ct));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.Failure.SqlState);
            Assert.Equal(0L, await Units(f, b));
        }
        Assert.Equal(a, await Worker(f).RunAsync(lease, Ct));
        await DrainAll(f);
        Assert.Equal(5L, await Units(f, a)); Assert.Equal(2L, await Units(f, b));
    }
    [Fact]
    public async Task ConcurrentDrainJobsAllocateEachOrderOnceAndRejectCompletedLeaseReplay()
    {
        await using var f = await Fixture();
        Guid a = await Purchase(f, 0, 5), b = await Purchase(f, 1, 2);
        await Restock(f, 7);
        JobLease first = await Claim(f), second = await Claim(f);
        await Task.WhenAll(Worker(f).RunAsync(first, Ct), Worker(f).RunAsync(second, Ct));
        await DrainAll(f);
        Assert.Equal(DbFailureKind.LeaseLost, (await Assert.ThrowsAsync<DbOperationException>(() => Worker(f).RunAsync(first, Ct))).Failure.Kind);
        Assert.Equal(5L, await Units(f, a)); Assert.Equal(2L, await Units(f, b));
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='prepare_manifest'", Ct));
    }
    [Fact]
    public async Task PendingAdministratorAuditBrandReferenceCannotDeadlockWholeHeadAllocation()
    {
        await using var f = await Fixture();
        Guid order = await Purchase(f, 0, 5); await Restock(f, 5);
        JobLease lease = await Claim(f);
        var admin = await f.AdminAsync(Ct);
        long revision = await Revision(f, order);
        await using var audit = await f.Sessions.BeginAsync(Ct);
        Assert.True(await AdminAuditStore.AppendAsync(audit, admin, new(Guid.NewGuid(), AuditAction.HoldPreorder, AuditEntity.Order, order, f.Brands[0], AuditReason.OperatorReview, Guid.NewGuid(), new byte[32], revision, revision + 1), Ct));
        await using (var incompatible = await new DbSessionFactory(f.Database.Data, new() { LockTimeout = TimeSpan.FromMilliseconds(100) }).BeginAsync(Ct))
            Assert.Equal(DbFailureKind.Busy, (await Assert.ThrowsAsync<DbOperationException>(() => incompatible.QuerySingleAsync(new DbStatement("SELECT id FROM shop.brands WHERE id=$1 FOR UPDATE", DbValue.Uuid(f.Brands[0])), r => r.GetGuid(0), Ct))).Failure.Kind);
        Assert.Equal(order, await Worker(f).RunAsync(lease, Ct));
        Assert.Equal(5L, await Units(f, order));
    }
    [Fact]
    public async Task DirectRestockContentIsAcquiredWithoutCipherRecovery()
    {
        await using var f = await Fixture();
        Guid order = await Purchase(f, 0, 5);
        await Restock(f, 5);
        await using (var edit = await f.Sessions.BeginAsync(Ct))
        {
            Guid item = await edit.QuerySingleAsync(new DbStatement("SELECT id FROM shop.inventory_items ORDER BY id DESC LIMIT 1"), r => r.GetGuid(0), Ct);
            await edit.ExecuteAsync(new DbStatement("UPDATE shop.inventory_items SET credential_content=$1 WHERE id=$2", DbValue.Binary([1]), DbValue.Uuid(item)), Ct);
            await edit.CommitAsync(Ct);
        }
        JobLease lease = await Claim(f);
        Assert.Equal(order, await Worker(f).RunAsync(lease, Ct));
        Assert.Equal(5L, await Units(f, order));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments WHERE order_id='" + order.ToString("D") + "' AND delivered_content=decode('01','hex')", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='available'", Ct));
    }
    [Fact]
    public async Task MissingTrustedHeadBrandFailsClosedAndNeverUsesTheNextBrandsStock()
    {
        await using var f = await Fixture();
        await Purchase(f, 0, 5); await Purchase(f, 1, 2); await Restock(f, 7);
        JobLease lease = await Claim(f);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new LocalPreorderWorker(new(f.Sessions), [f.Context(1)]).RunAsync(lease, Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments", Ct));
        Assert.NotNull(await Worker(f).RunAsync(lease, Ct));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OlderUnpaidQuoteDoesNotBlockAndExternalSettlementJoinsAtPaymentTime(bool bank)
    {
        await using var f = await Fixture();
        var payment = new PaymentSettlementFixture(f);
        Guid order, id; string? origin = null; decimal due = 0;
        if (bank)
        {
            await payment.EnableBankAsync(Ct);
            (id, order) = await payment.BankOrderAsync(Ct);
        }
        else
        {
            await payment.EnableAsync(Ct);
            var invoice = await payment.OrderAsync(5, ct: Ct);
            order = invoice.Order; id = invoice.Payment; origin = invoice.Origin; due = invoice.Due;
        }
        Guid paid = await Purchase(f, 1, 2);
        await Restock(f, 2); await DrainAll(f);
        Assert.Equal(2L, await Units(f, paid)); Assert.Equal(0L, await Units(f, order));
        if (bank)
        {
            await payment.Manual.SubmitAsync(f.Context(0), CheckoutFixture.Actor(0), id, "protected:synthetic/fifo-receipt", Ct);
            long revision = await Revision(f, order); Guid command = Guid.NewGuid();
            var admin = await f.AdminAsync(Ct);
            Assert.Equal(ManualDepositOutcome.ConfirmedPaid, await payment.Manual.DecideAsync(f.Context(0), admin, id, true, revision, command, "Public verified receipt", Ct));
            for (int i = 0; i < 100; i++) Assert.Equal(ManualDepositOutcome.Replayed, await payment.Manual.DecideAsync(f.Context(0), admin, id, true, revision, command, "Public verified receipt", Ct));
        }
        else
        {
            await payment.CaptureAsync(id, origin!, due, ct: Ct);
            Assert.Equal(ExternalSettlementRoute.LocalPreorder, (await payment.Store.SettleAsync(f.Context(0), id, PaymentSettlementFixture.Lookup, Ct)).Route);
            Assert.Equal(PaymentSettlementOutcome.Replayed, (await payment.Store.SettleAsync(f.Context(0), id, (_, _) => throw new InvalidOperationException("Unexpected replay lookup."), Ct)).Outcome);
        }
        await DrainAll(f);
        Assert.Equal(0L, await Units(f, order));
        await Restock(f, bank ? 1 : 5); await DrainAll(f);
        Assert.Equal(bank ? 1L : 5L, await Units(f, order));
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='paid' AND fulfillment_status='acquiring'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='expire_order_payment' AND status IN ('pending','retry','leased')", Ct));
    }
    [Fact]
    public async Task ExpiredWorkerLeaseCannotAllocateAndRestartReclaimsTheSameDurableJob()
    {
        await using var f = await Fixture();
        Guid order = await Purchase(f, 0, 5); await Restock(f, 5);
        JobLease old = await Claim(f);
        await using (var crash = await f.Sessions.BeginAsync(Ct))
        {
            await crash.ExecuteAsync(new DbStatement("UPDATE shop.jobs SET lease_until=clock_timestamp()-INTERVAL '1 second' WHERE id=$1", DbValue.Uuid(old.Id)), Ct);
            await crash.CommitAsync(Ct);
        }
        Assert.Equal(DbFailureKind.LeaseLost, (await Assert.ThrowsAsync<DbOperationException>(() => Worker(f).RunAsync(old, Ct))).Failure.Kind);
        Assert.Equal(0L, await Units(f, order));
        JobLease recovered = await Claim(f);
        Assert.Equal(old.Id, recovered.Id); Assert.Equal(old.Attempt + 1, recovered.Attempt);
        Assert.Equal(order, await Worker(f).RunAsync(recovered, Ct));
        Assert.Equal(DbFailureKind.LeaseLost, (await Assert.ThrowsAsync<DbOperationException>(() => Worker(f).RunAsync(old, Ct))).Failure.Kind);
        Assert.Equal(5L, await Units(f, order));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndAllocationRaceKeepsEitherWholeGoodsOrOneTerminalResolution(bool hold)
    {
        await using var f = await Fixture();
        var admin = await f.AdminAsync(Ct);
        await using (var issue = await f.Sessions.BeginAsync(Ct))
        {
            await new PricingStore(f.Sessions).IssueAsync(issue, f.Context(0), admin, new(Guid.NewGuid(), "PUBLIC-RACE-FIFO", CouponKind.Goodwill, 50, f.Product, f.Users[0], MaximumUses: 5), Guid.NewGuid(), Ct);
            await issue.CommitAsync(Ct);
        }
        Guid order = await Purchase(f, 0, 5); await Restock(f, 5);
        var resolution = new OrderResolution(hold ? "pause_preorder" : "cancel_local_preorder", await Revision(f, order), "Public cancellation race", Guid.NewGuid());
        var store = new OrderAdministrationStore(f.Sessions);
        var cancel = Record.ExceptionAsync(() => store.ResolveAsync(admin, f.Context(0), order, resolution, Ct));
        await Worker(f).RunAsync(await Claim(f), Ct);
        Exception? error = await cancel;
        if (error is null)
        {
            for (int i = 0; i < 100; i++) await store.ResolveAsync(admin, f.Context(0), order, resolution, Ct);
            Assert.Equal(0L, await Units(f, order));
            if (hold)
            {
                Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE hold_at IS NOT NULL", Ct));
                await store.ResolveAsync(admin, f.Context(0), order, new("resume_preorder", await Revision(f, order), "Public race resume", Guid.NewGuid()), Ct);
                await DrainAll(f); Assert.Equal(5L, await Units(f, order));
            }
            else Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE resolution_kind='local_preorder_cancellation' AND fulfillment_status='cancelled'", Ct));
        }
        else
        {
            Assert.IsType<InvalidOperationException>(error);
            Assert.Equal(5L, await Units(f, order));
            Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE resolution_kind IS NOT NULL", Ct));
        }
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE units_covered=5 AND status='" + (!hold && error is null ? "released" : "held") + "'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit' AND signed_amount=-25", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE signed_amount>0 AND movement_kind<>'admin_adjustment'", Ct));
    }
    [Fact]
    public async Task SupportAndCancelledClaimReplaysPreserveRedeemedFactsOwnerOverridesAndFinancialHistory()
    {
        await using var f = await Fixture();
        var admin = await f.AdminAsync(Ct);
        var pricing = new PricingStore(f.Sessions);
        Guid coupon = Guid.NewGuid();
        await using (var issue = await f.Sessions.BeginAsync(Ct))
        {
            await pricing.IssueAsync(issue, f.Context(0), admin, new(coupon, "PUBLIC-CANCEL-FIFO", CouponKind.Goodwill, 50, f.Product, f.Users[0], MaximumUses: 7), Guid.NewGuid(), Ct);
            await issue.CommitAsync(Ct);
        }
        Guid spent = await Purchase(f, 0, 2);
        await Restock(f, 2); await DrainAll(f);
        await new DeliveryStore(f.Sessions).PrepareAsync(f.Context(0), await Claim(f, JobKind.PrepareManifest), Ct);
        Guid waiting = await Purchase(f, 0, 5);
        Assert.Equal(1, await pricing.EditAsync(f.Context(0), admin, coupon, 0, new(10, 0, 0, 1, null, DateTime.UtcNow.AddMinutes(-1), false, f.Product, f.Users[0]), AuditReason.OperatorReview, Guid.NewGuid(), Ct));
        long revision = await Revision(f, waiting);
        await using (var support = await f.Sessions.BeginAsync(Ct))
        {
            var ticket = await SupportStore.OpenTicketAsync(support, f.Context(0), CheckoutFixture.Actor(0), waiting, Ct);
            Assert.True(ticket.Created);
            Assert.False((await SupportStore.OpenTicketAsync(support, f.Context(0), CheckoutFixture.Actor(0), waiting, Ct)).Created);
            await support.CommitAsync(Ct);
        }
        Assert.Equal(revision, await Revision(f, waiting));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.support_tickets WHERE category='preorder_cancellation'", Ct));
        await using (var fault = f.Database.Data.CreateCommand("CREATE FUNCTION shop.reject_fifo_release() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'Public synthetic release failure' USING ERRCODE='23514'; END $$; CREATE CONSTRAINT TRIGGER reject_fifo_release AFTER UPDATE ON shop.discount_uses DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (NEW.status='released') EXECUTE FUNCTION shop.reject_fifo_release()")) await fault.ExecuteNonQueryAsync(Ct);
        var store = new OrderAdministrationStore(f.Sessions);
        var resolution = new OrderResolution("cancel_local_preorder", revision, "Public outside-shop resolution", Guid.NewGuid());
        Assert.Equal(DbFailureKind.Constraint, (await Assert.ThrowsAsync<DbOperationException>(() => store.ResolveAsync(admin, f.Context(0), waiting, resolution, Ct))).Failure.Kind);
        Assert.Equal(revision, await Revision(f, waiting));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='held' AND units_covered=5", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.audit_events WHERE action='cancel_preorder'", Ct));
        await using (var repair = f.Database.Data.CreateCommand("DROP TRIGGER reject_fifo_release ON shop.discount_uses; DROP FUNCTION shop.reject_fifo_release()")) await repair.ExecuteNonQueryAsync(Ct);
        await store.ResolveAsync(admin, f.Context(0), waiting, resolution, Ct);
        for (int i = 0; i < 100; i++) await store.ResolveAsync(admin, f.Context(0), waiting, resolution, Ct);
        long spentRevision = await Revision(f, spent);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ResolveAsync(admin, f.Context(0), spent, new("cancel_local_preorder", spentRevision, "Public invalid completed cancellation", Guid.NewGuid()), Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='released' AND units_covered=5", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='redeemed' AND units_covered=2", Ct));
        Assert.Equal(2L, await f.CountAsync("SELECT redeemed_count FROM shop.discounts", Ct));
        Assert.Equal(-1L, await f.CountAsync("SELECT usage_adjustment_units FROM shop.discounts", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discounts WHERE NOT active AND ends_at<clock_timestamp() AND max_total_uses=0", Ct));
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE signed_amount>0 AND movement_kind<>'admin_adjustment'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE resolution_kind='local_preorder_cancellation' AND resolution_wallet_entry_id IS NULL AND payment_status='paid'", Ct));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LockedOrHeldHeadPreventsCompetingLocalAndLocalFirstSupplierStockBypass(bool held)
    {
        await using var f = await Fixture();
        Guid head = await Purchase(f, 0, 5);
        var admin = await f.AdminAsync(Ct);
        var orders = new OrderAdministrationStore(f.Sessions);
        if (held) await orders.ResolveAsync(admin, f.Context(0), head, new("pause_preorder", await Revision(f, head), "Public FIFO hold", Guid.NewGuid()), Ct);
        await Restock(f, 3);
        await using (var edit = await f.Sessions.BeginAsync(Ct))
        {
            await edit.ExecuteAsync(new DbStatement("UPDATE shop.products SET availability_policy='immediate',revision=revision+1"), Ct);
            await edit.CommitAsync(Ct);
        }
        await using (var locked = await f.Sessions.BeginAsync(Ct))
        {
            await locked.QuerySingleAsync(new DbStatement("SELECT id FROM shop.orders WHERE id=$1 FOR UPDATE", DbValue.Uuid(head)), r => r.GetGuid(0), Ct);
            Assert.Equal(CheckoutOutcome.Unavailable, (await f.Store.PurchaseLocalAsync(f.Context(1), CheckoutFixture.Actor(1), f.Request() with { ProductRevision = 2 }, Ct)).Outcome);
            await SupplierFixture.ConfigureAsync(f, Ct);
            var service = new WalletCheckoutService(f.Sessions, f.Store, new PaymentSettlementFixture(f).Catalog);
            var remote = await service.PurchaseAsync(f.Context(1), CheckoutFixture.Actor(1), new(Guid.CreateVersion7(), f.Product, 1), (_, _, _) => Task.FromResult(ChannelMembership.Member), Ct);
            Assert.Equal(CheckoutOutcome.Purchased, remote.Outcome);
            Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.supplier_requests WHERE requested_quantity=1", Ct));
            Assert.Equal(3L, await f.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='available'", Ct));
            Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments", Ct));
        }
        await Restock(f, 2); await DrainAll(f);
        if (held)
        {
            Assert.Equal(0L, await Units(f, head));
            await orders.ResolveAsync(admin, f.Context(0), head, new("resume_preorder", await Revision(f, head), "Public FIFO resume", Guid.NewGuid()), Ct);
            await DrainAll(f);
        }
        Assert.Equal(5L, await Units(f, head));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='available'", Ct));
    }
    [Fact]
    public async Task ConcurrentCryptoSettlementAndDrainKeepPaidPriorityAndNeverReserveTheLargerRemainder()
    {
        await using var f = await Fixture();
        var payment = new PaymentSettlementFixture(f);
        await payment.EnableAsync(Ct);
        var invoice = await payment.OrderAsync(5, ct: Ct);
        Guid firstPaid = await Purchase(f, 1, 2);
        await Restock(f, 5);
        await payment.CaptureAsync(invoice.Payment, invoice.Origin, invoice.Due, ct: Ct);
        JobLease lease = await Claim(f);
        var settled = payment.Store.SettleAsync(f.Context(0), invoice.Payment, PaymentSettlementFixture.Lookup, Ct);
        await Task.WhenAll(settled, Worker(f).RunAsync(lease, Ct));
        Assert.Equal(ExternalSettlementRoute.LocalPreorder, (await settled).Route);
        await DrainAll(f);
        Assert.Equal(2L, await Units(f, firstPaid)); Assert.Equal(0L, await Units(f, invoice.Order));
        Assert.Equal(3L, await f.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='available'", Ct));
        await Restock(f, 2); await DrainAll(f);
        Assert.Equal(5L, await Units(f, invoice.Order));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit' AND signed_amount=-20", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE signed_amount>0 AND movement_kind<>'admin_adjustment'", Ct));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentBankOrFreeAcceptanceAndDrainKeepEarlierPaidMembershipAhead(bool free)
    {
        await using var f = await Fixture();
        var admin = await f.AdminAsync(Ct);
        var payment = new PaymentSettlementFixture(f);
        Guid order, paymentId = Guid.Empty;
        if (free)
        {
            await using var issue = await f.Sessions.BeginAsync(Ct);
            await new PricingStore(f.Sessions).IssueAsync(issue, f.Context(0), admin, new(Guid.NewGuid(), "PUBLIC-FREE-RACE", CouponKind.Goodwill, 100, f.Product, f.Users[0], MaximumUses: 5), Guid.NewGuid(), Ct);
            await issue.CommitAsync(Ct); order = Guid.CreateVersion7();
        }
        else
        {
            await payment.EnableBankAsync(Ct);
            (paymentId, order) = await payment.BankOrderAsync(Ct);
            await payment.Manual.SubmitAsync(f.Context(0), CheckoutFixture.Actor(0), paymentId, "protected:synthetic/race-receipt", Ct);
        }
        Guid earlier = await Purchase(f, 1, 2);
        await Restock(f, free ? 5 : 2);
        JobLease lease = await Claim(f);
        Task acceptance;
        if (free)
        {
            var service = new WalletCheckoutService(f.Sessions, f.Store, payment.Catalog);
            acceptance = service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), new(order, f.Product, 5), (_, _, _) => Task.FromResult(ChannelMembership.Member), Ct);
        }
        else acceptance = payment.Manual.DecideAsync(f.Context(0), admin, paymentId, true, await Revision(f, order), Guid.NewGuid(), "Public verified race receipt", Ct);
        await Task.WhenAll(acceptance, Worker(f).RunAsync(lease, Ct));
        await DrainAll(f);
        Assert.Equal(2L, await Units(f, earlier)); Assert.Equal(0L, await Units(f, order));
        Assert.Equal(free ? 3L : 0L, await f.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='available'", Ct));
        await Restock(f, free ? 2 : 1); await DrainAll(f);
        Assert.Equal(free ? 5L : 1L, await Units(f, order));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit' AND signed_amount=-20", Ct));
        if (free) Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='held' AND units_covered=5", Ct));
        else Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.payments WHERE status='settled'", Ct));
    }
    [Fact]
    public async Task CompletedPreorderFileRecoversUnknownTransportOnlyAfterAuditWithoutRedeemingAgain()
    {
        await using var f = await Fixture();
        var admin = await f.AdminAsync(Ct);
        await using (var issue = await f.Sessions.BeginAsync(Ct))
        {
            await new PricingStore(f.Sessions).IssueAsync(issue, f.Context(0), admin, new(Guid.NewGuid(), "PUBLIC-FILE-FIFO", CouponKind.Goodwill, 100, f.Product, f.Users[0], MaximumUses: 5), Guid.NewGuid(), Ct);
            await issue.CommitAsync(Ct);
        }
        Guid order = await Purchase(f, 0, 5);
        await new AdminCatalogStore(f.Sessions).ImportAsync(admin, f.Product, Enumerable.Range(0, 5).Select(_ => new InventoryImportItem(Guid.NewGuid(), "Public file credential " + new string('s', 1000) + Guid.NewGuid())).ToArray(), Guid.NewGuid(), Ct);
        await DrainAll(f);
        var store = new DeliveryStore(f.Sessions);
        Assert.Null(await store.PrepareAsync(f.Context(0), await Claim(f, JobKind.PrepareManifest), Ct));
        Guid delivery = await store.AuthorizeSendRecoveryAsync(f.Context(0), admin, order, Guid.NewGuid(), Ct);
        JobLease first = await Claim(f, JobKind.SendTelegram);
        Guid generation = Guid.Empty; byte[] digest = [];
        await new LocalDeliveryWorker(store).RunAsync(f.Context(0), first, (dispatch, _) =>
        {
            Assert.True(dispatch.IsFile); Assert.Equal(order, dispatch.Order); Assert.Equal(delivery, dispatch.Id);
            generation = dispatch.Generation; digest = SHA256.HashData(dispatch.Content.Span);
            return Task.FromResult(new DeliveryOutcome(DeliveryOutcomeKind.Unknown));
        }, Ct);
        await using (var denied = await f.Sessions.BeginAsync(Ct)) Assert.Null(await OutboxStore.ClaimAsync(denied, JobKind.SendTelegram, Guid.NewGuid(), TimeSpan.FromSeconds(30), Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.ReadManifestAsync(f.Context(1), CheckoutFixture.Actor(1), order, Ct));
        Assert.Equal(delivery, await store.AuthorizeSendRecoveryAsync(f.Context(0), admin, order, Guid.NewGuid(), Ct));
        await new LocalDeliveryWorker(store).RunAsync(f.Context(0), await Claim(f, JobKind.SendTelegram), (dispatch, _) =>
        {
            Assert.True(dispatch.IsFile); Assert.Equal(generation, dispatch.Generation); Assert.Equal(1001, dispatch.BotId); Assert.Equal(CheckoutFixture.Actor(0), dispatch.ChatId);
            Assert.Equal(digest, SHA256.HashData(dispatch.Content.Span));
            return Task.FromResult(new DeliveryOutcome(DeliveryOutcomeKind.Succeeded, 901));
        }, Ct);
        await store.RecordOutcomeAsync(f.Context(0), first, new(DeliveryOutcomeKind.Succeeded, 901), Ct);
        Assert.Equal(5L, await f.CountAsync("SELECT redeemed_count FROM shop.discounts", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='redeemed' AND units_covered=5", Ct));
        Assert.Equal(5L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments WHERE status='released'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE fulfillment_status='completed'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", Ct));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KilledWorkerProcessRecoversThroughAFreshSchedulerWithoutPartialOrRepeatedAllocation(bool afterCommit) =>
        await PreorderProcessQualification.RunAsync(afterCommit, Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Platform.AotSmoke/bin/Release/net10.0/Platform.AotSmoke.dll")), Ct);
    [Fact]
    public async Task LostCancellationCommitAcknowledgementReplaysOneWholeReleaseAndLetsTheNextBrandDrain()
    {
        await using var f = await Fixture();
        var admin = await f.AdminAsync(Ct);
        await using (var issue = await f.Sessions.BeginAsync(Ct))
        {
            await new PricingStore(f.Sessions).IssueAsync(issue, f.Context(0), admin, new(Guid.NewGuid(), "PUBLIC-CANCEL-COMMIT", CouponKind.Goodwill, 50, f.Product, f.Users[0], MaximumUses: 5), Guid.NewGuid(), Ct);
            await issue.CommitAsync(Ct);
        }
        Guid first = await Purchase(f, 0, 5), next = await Purchase(f, 1, 2);
        await Restock(f, 3); await DrainAll(f);
        var resolution = new OrderResolution("cancel_local_preorder", await Revision(f, first), "Public outside-shop cancellation", Guid.NewGuid());
        var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SHOP_TEST_POSTGRES_ADMIN") ?? throw new InvalidOperationException("Synthetic control identity is missing."))
        { Database = new NpgsqlConnectionStringBuilder(f.Database.Data.ConnectionString).Database };
        await using var proxy = new DropCommitReplyProxy(options.ConnectionString);
        await using var transport = proxy.CreateClientDataSource();
        var error = await Assert.ThrowsAsync<DbOperationException>(() => new OrderAdministrationStore(new(transport)).ResolveAsync(admin, f.Context(0), first, resolution, Ct));
        Assert.Equal(DbFailureKind.OutcomeUnknown, error.Failure.Kind);
        Assert.Equal(DbRecovery.ReconcileBeforeRetry, error.Failure.Recovery);
        await proxy.CommitObserved.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var restarted = new OrderAdministrationStore(new(f.Database.Data));
        for (int i = 0; i < 100; i++) await restarted.ResolveAsync(admin, f.Context(0), first, resolution, Ct);
        await DrainAll(f);
        Assert.Equal(0L, await Units(f, first)); Assert.Equal(2L, await Units(f, next));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='released' AND units_covered=5", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT redeemed_count FROM shop.discounts", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.audit_events WHERE action='cancel_preorder'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='paid' AND fulfillment_status='cancelled' AND resolution_wallet_entry_id IS NULL", Ct));
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE signed_amount>0 AND movement_kind<>'admin_adjustment'", Ct));
    }
    [Fact]
    public async Task DeferredManifestJobFailureRollsBackAcquisitionLeaseCompletionAndContinuationTogether()
    {
        await using var f = await Fixture();
        Guid order = await Purchase(f, 0, 5); await Restock(f, 5);
        JobLease lease = await Claim(f); long revision = await Revision(f, order);
        await using (var fault = f.Database.Data.CreateCommand("CREATE FUNCTION shop.reject_preorder_manifest() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'Public synthetic manifest failure' USING ERRCODE='23514'; END $$; CREATE CONSTRAINT TRIGGER reject_preorder_manifest AFTER INSERT ON shop.jobs DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (NEW.job_kind='prepare_manifest') EXECUTE FUNCTION shop.reject_preorder_manifest()")) await fault.ExecuteNonQueryAsync(Ct);
        Assert.Equal(DbFailureKind.Constraint, (await Assert.ThrowsAsync<DbOperationException>(() => Worker(f).RunAsync(lease, Ct))).Failure.Kind);
        Assert.Equal(0L, await Units(f, order)); Assert.Equal(revision, await Revision(f, order));
        Assert.Equal(5L, await f.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='available'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='prepare_manifest' OR (job_kind='drain_local_preorders' AND status='succeeded')", Ct));
        await using (var read = await f.Sessions.BeginAsync(Ct))
            Assert.Equal(("leased", lease.Attempt), await read.QuerySingleAsync(new DbStatement("SELECT status,attempts FROM shop.jobs WHERE id=$1", DbValue.Uuid(lease.Id)), r => (r.GetString(0), r.GetInt64(1)), Ct));
        await using (var repair = f.Database.Data.CreateCommand("DROP TRIGGER reject_preorder_manifest ON shop.jobs; DROP FUNCTION shop.reject_preorder_manifest()")) await repair.ExecuteNonQueryAsync(Ct);
        Assert.Equal(order, await Worker(f).RunAsync(lease, Ct)); await DrainAll(f);
        Assert.Equal(5L, await Units(f, order));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='prepare_manifest'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit' AND signed_amount=-50", Ct));
    }
    [Theory]
    [InlineData(31)]
    [InlineData(1051200)]
    public async Task PaidPreorderOutlivesOriginalInvoiceWhileIndependentUnpaidPreorderExpires(int ageMinutes)
    {
        await using var f = await Fixture();
        var payment = new PaymentSettlementFixture(f); await payment.EnableAsync(Ct);
        var paid = await PaymentDeadlineQualification.SeedAsync(f, 0, true, proof: true, ct: Ct, preorder: true, age: TimeSpan.FromMinutes(ageMinutes));
        JobLease oldExpiry = await Claim(f, JobKind.ExpireOrderPayment);
        await payment.CaptureAsync(paid.Payment, paid.Origin, paid.Due, ct: Ct);
        Assert.Equal(ExternalSettlementRoute.LocalPreorder, (await payment.Store.SettleAsync(f.Context(0), paid.Payment, PaymentSettlementFixture.Lookup, Ct)).Route);
        long revision = await Revision(f, paid.Order);
        Assert.Equal(PaymentExpiryOutcome.Terminal, await new PaymentExpiryService(f.Sessions).RunAsync(f.Context(0), oldExpiry, Ct));
        var unpaid = await PaymentDeadlineQualification.SeedAsync(f, 1, true, ct: Ct, preorder: true);
        Assert.Equal(PaymentExpiryOutcome.Expired, await new PaymentExpiryService(f.Sessions).RunAsync(f.Context(1), await Claim(f, JobKind.ExpireOrderPayment), Ct));
        await DrainAll(f);
        Assert.Equal(revision, await Revision(f, paid.Order));
        Assert.Equal(0L, await Units(f, paid.Order)); Assert.Equal(0L, await Units(f, unpaid.Order));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE is_preorder AND payment_status='paid' AND fulfillment_status='waiting_stock' AND resolution_kind IS NULL", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE is_preorder AND payment_status='expired' AND resolution_kind='unpaid_expiry'", Ct));
        await Restock(f, 1); await DrainAll(f);
        Assert.Equal(1L, await Units(f, paid.Order)); Assert.Equal(0L, await Units(f, unpaid.Order));
        await using var read = await f.Sessions.BeginAsync(Ct);
        Assert.Equal(paid.Deadline, await read.QuerySingleAsync(new DbStatement("SELECT payment_due_at FROM shop.orders WHERE id=$1", DbValue.Uuid(paid.Order)), r => r.GetUtc(0), Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind<>'admin_adjustment'", Ct));
    }
    [Fact]
    public async Task CanonicalPreordersHaveNoFulfillmentDatesOrPaidExpiryJobs()
    {
        await using var f = await Fixture();
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM information_schema.columns WHERE table_schema='shop' AND table_name IN ('products','brand_products','orders') AND column_name ~ '(availability_(at|date|duration)|delivery_(at|date|deadline|duration|eta)|fulfillment_(at|date|deadline|duration|eta)|preorder_(at|date|deadline|duration|eta))'", Ct));
        Assert.Equal(new[] { nameof(JobKind.ExpireOrderPayment) }, Enum.GetNames<JobKind>().Where(n => n.Contains("Expir", StringComparison.OrdinalIgnoreCase)));
        Guid order = await Purchase(f, 0, 5);
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind LIKE '%expir%'", Ct));
        Assert.Equal(0L, await Units(f, order));
    }
    [Fact]
    public async Task ChangedHeadDefersToDurableContinuationWithoutLockingTheNextMembershipUnderProductLock()
    {
        await using var f = await Fixture();
        Guid first = await Purchase(f, 0, 5), next = await Purchase(f, 1, 2); await Restock(f, 2);
        JobLease lease = await Claim(f); var admin = await f.AdminAsync(Ct); Guid command = Guid.NewGuid();
        long revision = await Revision(f, first);
        var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SHOP_TEST_POSTGRES_ADMIN") ?? throw new InvalidOperationException("Synthetic control identity is missing."))
        { Database = new NpgsqlConnectionStringBuilder(f.Database.Data.ConnectionString).Database, ApplicationName = "public-fifo-head-change", Pooling = false };
        await using var data = new NpgsqlSlimDataSourceBuilder(options.ConnectionString).EnableArrays().Build();
        Task<Guid?> running;
        await using (var nextMember = await f.Sessions.BeginAsync(Ct))
        {
            await nextMember.QuerySingleAsync(new DbStatement("SELECT id FROM shop.users WHERE id=$1 FOR UPDATE", DbValue.Uuid(f.Users[1])), r => r.GetGuid(0), Ct);
            await using (var owner = await f.Sessions.BeginAsync(Ct))
            {
                Assert.True(await AdminAuditStore.AppendAsync(owner, admin, new(command, AuditAction.CancelPreorder, AuditEntity.Order, first, f.Brands[0], AuditReason.OperatorReview, command, new byte[32], revision, revision + 1), Ct));
                await owner.QuerySingleAsync(new DbStatement("SELECT id FROM shop.users WHERE id=$1 FOR UPDATE", DbValue.Uuid(f.Users[0])), r => r.GetGuid(0), Ct);
                await owner.QuerySingleAsync(new DbStatement("SELECT id FROM shop.products WHERE id=$1 FOR UPDATE", DbValue.Uuid(f.Product)), r => r.GetGuid(0), Ct);
                running = new LocalPreorderWorker(new(new(data, new() { LockTimeout = TimeSpan.FromSeconds(3) })), f.Contexts).RunAsync(lease, Ct);
                const string waiting = "SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND application_name='public-fifo-head-change' AND wait_event_type='Lock'";
                for (int i = 0; i < 100 && await f.CountAsync(waiting, Ct) == 0; i++) await Task.Delay(TimeSpan.FromMilliseconds(20), Ct);
                Assert.Equal(1L, await f.CountAsync(waiting, Ct));
                await owner.ExecuteAsync(new DbStatement("UPDATE shop.orders SET fulfillment_status='cancelled',resolution_kind='local_preorder_cancellation',resolution_admin_id=$1,resolution_at=clock_timestamp(),resolution_audit_event_id=$2,revision=revision+1,updated_at=clock_timestamp() WHERE id=$3", DbValue.Uuid(admin.AdminId), DbValue.Uuid(command), DbValue.Uuid(first)), Ct);
                await OutboxStore.EnqueueAsync(owner, JobIntent.Product(JobKind.DrainLocalPreorders, f.Product), command, DateTime.UtcNow, Ct);
                await owner.CommitAsync(Ct);
            }
            Assert.Null(await running);
            Assert.Equal(0L, await Units(f, first)); Assert.Equal(0L, await Units(f, next));
            await using var read = await f.Sessions.BeginAsync(Ct);
            Assert.Equal(1L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.jobs WHERE job_kind='drain_local_preorders' AND status='pending' AND dedupe_key=$1", DbValue.Text("drain_local_preorders:" + lease.Id.ToString("D"))), r => r.GetInt64(0), Ct));
        }
        await DrainAll(f);
        Assert.Equal(0L, await Units(f, first)); Assert.Equal(2L, await Units(f, next));
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE signed_amount>0 AND movement_kind<>'admin_adjustment'", Ct));
    }
    private static LocalPreorderWorker Worker(CheckoutFixture f) => new(new(f.Sessions), f.Contexts);
    private static async Task<CheckoutFixture> Fixture()
    {
        var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await using var policy = await f.Sessions.BeginAsync(Ct);
        await policy.ExecuteAsync(new DbStatement("UPDATE shop.products SET availability_policy='local_preorder',revision=revision+1"), Ct);
        await policy.CommitAsync(Ct); return f;
    }
    private static async Task<Guid> Purchase(CheckoutFixture f, int user, int quantity)
    {
        var service = new WalletCheckoutService(f.Sessions, f.Store, new CatalogStore(f.Sessions, [], (_, _) => throw new InvalidOperationException("Unexpected supplier call.")));
        var result = await service.PurchaseAsync(f.Context(user), CheckoutFixture.Actor(user), new(Guid.CreateVersion7(), f.Product, quantity), (_, _, _) => Task.FromResult(ChannelMembership.Member), Ct);
        Assert.Equal(CheckoutOutcome.Purchased, result.Outcome); return result.OrderId!.Value;
    }
    private static async Task Restock(CheckoutFixture f, int count) => await new AdminCatalogStore(f.Sessions).ImportAsync(await f.AdminAsync(Ct), f.Product, Enumerable.Range(0, count).Select(_ => new InventoryImportItem(Guid.NewGuid(), "Public FIFO stock " + Guid.NewGuid())).ToArray(), Guid.NewGuid(), Ct);
    private static async Task<JobLease> Claim(CheckoutFixture f, JobKind kind = JobKind.DrainLocalPreorders)
    {
        await using var claim = await f.Sessions.BeginAsync(Ct);
        JobLease? lease = await OutboxStore.ClaimAsync(claim, kind, Guid.NewGuid(), TimeSpan.FromSeconds(30), Ct);
        Assert.NotNull(lease); await claim.CommitAsync(Ct); return lease;
    }
    private static async Task DrainAll(CheckoutFixture f)
    {
        for (int i = 0; i < 30; i++)
        {
            JobLease? lease;
            await using (var claim = await f.Sessions.BeginAsync(Ct))
            {
                lease = await OutboxStore.ClaimAsync(claim, JobKind.DrainLocalPreorders, Guid.NewGuid(), TimeSpan.FromSeconds(30), Ct);
                await claim.CommitAsync(Ct);
            }
            if (lease is null) return;
            await Worker(f).RunAsync(lease, Ct);
        }
        throw new InvalidOperationException("Unexpected unbounded drain work.");
    }
    private static async Task<long> Units(CheckoutFixture f, Guid order)
    { await using var read = await f.Sessions.BeginAsync(Ct); return await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.fulfillments WHERE order_id=$1", DbValue.Uuid(order)), r => r.GetInt64(0), Ct); }
    private static Task<long> Revision(CheckoutFixture f, Guid order) => AdminCatalogAndResolutionTests.Revision(f, order);
}
