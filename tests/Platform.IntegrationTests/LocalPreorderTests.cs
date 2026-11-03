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
            await new PricingStore(f.Sessions, f.Secrets).IssueAsync(issue, f.Context(0), admin, new(coupon, "PUBLIC-FIFO", CouponKind.Promotion, percent, f.Product, f.Users[0], MaximumUses: 5), Guid.NewGuid(), Ct);
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
        var delivery = new DeliveryStore(f.Sessions, f.Secrets);
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
        var store = new OrderAdministrationStore(f.Sessions, f.Secrets);
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
            var worker = new LocalPreorderWorker(new(sessions, f.Secrets), f.Contexts);
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
    public async Task DecryptionFailureRollsBackAllUnitsOrderRevisionAndOutboxAndCanRetry()
    {
        await using var f = await Fixture();
        Guid order = await Purchase(f, 0, 5);
        await Restock(f, 5);
        Guid item; byte[] content;
        await using (var corrupt = await f.Sessions.BeginAsync(Ct))
        {
            var data = await corrupt.QuerySingleAsync(new DbStatement("SELECT id,credential_ciphertext FROM shop.inventory_items ORDER BY id DESC LIMIT 1"), r => (Id: r.GetGuid(0), Content: r.GetBinary(1)), Ct);
            item = data.Id; content = data.Content;
            await corrupt.ExecuteAsync(new DbStatement("UPDATE shop.inventory_items SET credential_ciphertext=$1 WHERE id=$2", DbValue.Binary([1]), DbValue.Uuid(item)), Ct);
            await corrupt.CommitAsync(Ct);
        }
        JobLease lease = await Claim(f);
        long revision = await Revision(f, order);
        await Assert.ThrowsAsync<CipherRejectedException>(() => Worker(f).RunAsync(lease, Ct));
        Assert.Equal(0L, await Units(f, order));
        Assert.Equal(revision, await Revision(f, order));
        Assert.Equal(5L, await f.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='available'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='prepare_manifest'", Ct));
        await using (var repair = await f.Sessions.BeginAsync(Ct))
        {
            await repair.ExecuteAsync(new DbStatement("UPDATE shop.inventory_items SET credential_ciphertext=$1 WHERE id=$2", DbValue.Binary(content), DbValue.Uuid(item)), Ct);
            await repair.CommitAsync(Ct);
        }
        Assert.Equal(order, await Worker(f).RunAsync(lease, Ct));
        Assert.Equal(5L, await Units(f, order));
    }
    [Fact]
    public async Task MissingTrustedHeadBrandFailsClosedAndNeverUsesTheNextBrandsStock()
    {
        await using var f = await Fixture();
        await Purchase(f, 0, 5); await Purchase(f, 1, 2); await Restock(f, 7);
        JobLease lease = await Claim(f);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new LocalPreorderWorker(new(f.Sessions, f.Secrets), [f.Context(1)]).RunAsync(lease, Ct));
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
            await new PricingStore(f.Sessions, f.Secrets).IssueAsync(issue, f.Context(0), admin, new(Guid.NewGuid(), "PUBLIC-RACE-FIFO", CouponKind.Goodwill, 50, f.Product, f.Users[0], MaximumUses: 5), Guid.NewGuid(), Ct);
            await issue.CommitAsync(Ct);
        }
        Guid order = await Purchase(f, 0, 5); await Restock(f, 5);
        var resolution = new OrderResolution(hold ? "pause_preorder" : "cancel_local_preorder", await Revision(f, order), "Public cancellation race", Guid.NewGuid());
        var store = new OrderAdministrationStore(f.Sessions, f.Secrets);
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
        var pricing = new PricingStore(f.Sessions, f.Secrets);
        Guid coupon = Guid.NewGuid();
        await using (var issue = await f.Sessions.BeginAsync(Ct))
        {
            await pricing.IssueAsync(issue, f.Context(0), admin, new(coupon, "PUBLIC-CANCEL-FIFO", CouponKind.Goodwill, 50, f.Product, f.Users[0], MaximumUses: 7), Guid.NewGuid(), Ct);
            await issue.CommitAsync(Ct);
        }
        Guid spent = await Purchase(f, 0, 2);
        await Restock(f, 2); await DrainAll(f);
        await new DeliveryStore(f.Sessions, f.Secrets).PrepareAsync(f.Context(0), await Claim(f, JobKind.PrepareManifest), Ct);
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
        var store = new OrderAdministrationStore(f.Sessions, f.Secrets);
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
        var orders = new OrderAdministrationStore(f.Sessions, f.Secrets);
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
    [Fact]
    public async Task CompletedPreorderFileRecoversUnknownTransportOnlyAfterAuditWithoutRedeemingAgain()
    {
        await using var f = await Fixture();
        var admin = await f.AdminAsync(Ct);
        await using (var issue = await f.Sessions.BeginAsync(Ct))
        {
            await new PricingStore(f.Sessions, f.Secrets).IssueAsync(issue, f.Context(0), admin, new(Guid.NewGuid(), "PUBLIC-FILE-FIFO", CouponKind.Goodwill, 100, f.Product, f.Users[0], MaximumUses: 5), Guid.NewGuid(), Ct);
            await issue.CommitAsync(Ct);
        }
        Guid order = await Purchase(f, 0, 5);
        await new AdminCatalogStore(f.Sessions, f.Secrets).ImportAsync(admin, f.Product, Enumerable.Range(0, 5).Select(_ => new InventoryImportItem(Guid.NewGuid(), "Public file credential " + new string('s', 1000) + Guid.NewGuid())).ToArray(), Guid.NewGuid(), Ct);
        await DrainAll(f);
        var store = new DeliveryStore(f.Sessions, f.Secrets);
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
    private static LocalPreorderWorker Worker(CheckoutFixture f) => new(new(f.Sessions, f.Secrets), f.Contexts);
    private static async Task<CheckoutFixture> Fixture()
    {
        var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await using var policy = await f.Sessions.BeginAsync(Ct);
        await policy.ExecuteAsync(new DbStatement("UPDATE shop.products SET availability_policy='local_preorder',revision=revision+1"), Ct);
        await policy.CommitAsync(Ct); return f;
    }
    private static async Task<Guid> Purchase(CheckoutFixture f, int user, int quantity)
    {
        var service = new WalletCheckoutService(f.Sessions, f.Store, new CatalogStore(f.Sessions, f.Secrets, [], (_, _) => throw new InvalidOperationException("Unexpected supplier call.")));
        var result = await service.PurchaseAsync(f.Context(user), CheckoutFixture.Actor(user), new(Guid.CreateVersion7(), f.Product, quantity), (_, _, _) => Task.FromResult(ChannelMembership.Member), Ct);
        Assert.Equal(CheckoutOutcome.Purchased, result.Outcome); return result.OrderId!.Value;
    }
    private static async Task Restock(CheckoutFixture f, int count) => await new AdminCatalogStore(f.Sessions, f.Secrets).ImportAsync(await f.AdminAsync(Ct), f.Product, Enumerable.Range(0, count).Select(_ => new InventoryImportItem(Guid.NewGuid(), "Public FIFO stock " + Guid.NewGuid())).ToArray(), Guid.NewGuid(), Ct);
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
