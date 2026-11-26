using System.Security.Cryptography;
using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class AdminSupplierResolutionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task SafeRetryCarriesOriginalAuthorizationAndLaterCreditReturnsTheWholeClaimWithoutRebinding()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, stock: 3, ct: Ct);
        var fixture = new PaymentSettlementFixture(f);
        await fixture.EnableAsync(Ct);
        Guid source = await Supplier(f);
        var actor = await f.AdminAsync(Ct);
        Guid coupon = Guid.NewGuid();
        await using (var issue = await f.Sessions.BeginAsync(Ct))
        { await new PricingStore(f.Sessions).IssueAsync(issue, f.Context(0), actor, new(coupon, "PUBLIC-MIXED-RESOLVE", CouponKind.Goodwill, 50, f.Product, f.Users[0], MaximumUses: 5), Guid.NewGuid(), Ct); await issue.CommitAsync(Ct); }
        var order = await fixture.OrderAsync(5, ct: Ct);
        await fixture.CaptureAsync(order.Payment, order.Origin, order.Due, ct: Ct);
        await fixture.Store.SettleAsync(f.Context(0), order.Payment, PaymentSettlementFixture.Lookup, Ct);
        Guid request = await Fail(f, order.Order);
        await using (var forbidden = await f.Sessions.BeginAsync(Ct))
        {
            var rejected = await Assert.ThrowsAsync<DbOperationException>(() => OutboxStore.EnqueueAsync(forbidden, JobIntent.Supplier(JobKind.SubmitSupplier, f.Brands[0], request), Guid.NewGuid(), DateTime.UtcNow, Ct));
            Assert.Equal(DbFailureKind.Constraint, rejected.Failure.Kind);
        }
        var store = new OrderAdministrationStore(f.Sessions);
        long revision = await AdminCatalogAndResolutionTests.Revision(f, order.Order);
        long requestRevision = await RequestRevision(f, request);
        Guid retry = Guid.NewGuid();
        var command = new OrderResolution("retry_supplier", revision, "Public safe retry", retry, request, requestRevision);
        await store.ResolveAsync(actor, f.Context(0), order.Order, command, Ct);
        await store.ResolveAsync(actor, f.Context(0), order.Order, command, Ct);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE supplier_request_id='" + request.ToString("D") + "' AND audit_event_id='" + retry.ToString("D") + "' AND origin_revision=" + (requestRevision + 1), Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.supplier_requests WHERE product_supplier_source_id='" + source.ToString("D") + "' AND cost_baseline_amount=1 AND requested_quantity=2 AND status='pending'", Ct));
        await Fail(f, order.Order);
        var credit = new OrderResolution("credit_supplier_failure", await AdminCatalogAndResolutionTests.Revision(f, order.Order), "Public explicit terminal credit", Guid.NewGuid(), request, await RequestRevision(f, request));
        await store.ResolveAsync(actor, f.Context(0), order.Order, credit, Ct);
        await store.ResolveAsync(actor, f.Context(0), order.Order, credit, Ct);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='supplier_wallet_credit' AND signed_amount=25", Ct));
        Assert.Equal(3L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments WHERE status='retained_admin'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='released' AND units_covered=5", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT redeemed_count FROM shop.discounts", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='paid' AND fulfillment_status='compensated' AND release_authorized_at IS NULL", Ct));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownSupplierEffectsPreventBothRetryAndCreditUntilOriginalAttemptIsReconciled(bool missingProofCode)
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, stock: 0, ct: Ct);
        var fixture = new PaymentSettlementFixture(f);
        await fixture.EnableAsync(Ct);
        await Supplier(f);
        var order = await fixture.OrderAsync(ct: Ct);
        await fixture.CaptureAsync(order.Payment, order.Origin, order.Due, ct: Ct);
        await fixture.Store.SettleAsync(f.Context(0), order.Payment, PaymentSettlementFixture.Lookup, Ct);
        Guid request;
        await using (var read = await f.Sessions.BeginAsync(Ct)) request = await read.QuerySingleAsync(new DbStatement("SELECT id FROM shop.supplier_requests"), r => r.GetGuid(0), Ct);
        await using (var unknown = await f.Sessions.BeginAsync(Ct))
        {
            Guid attempt = Guid.NewGuid();
            await unknown.ExecuteAsync(new DbStatement("INSERT INTO shop.supplier_request_attempts(id,supplier_request_id,attempt_no,operation,max_charge_amount) VALUES ($1,$2,1,'submit',1.10)", DbValue.Uuid(attempt), DbValue.Uuid(request)), Ct);
            await unknown.ExecuteAsync(new DbStatement("UPDATE shop.supplier_request_attempts SET outcome=$2,evidence_ref=$3,finished_at=clock_timestamp() WHERE id=$1", DbValue.Uuid(attempt), DbValue.Text(missingProofCode ? "failed" : "unknown"), missingProofCode ? DbValue.Text("protected:synthetic/unclear-result") : DbValue.Null(NpgsqlTypes.NpgsqlDbType.Text)), Ct);
            await unknown.CommitAsync(Ct);
        }
        await Fail(f, order.Order);
        var actor = await f.AdminAsync(Ct);
        var store = new OrderAdministrationStore(f.Sessions);
        long revision = await AdminCatalogAndResolutionTests.Revision(f, order.Order);
        long requestRevision = await RequestRevision(f, request);
        foreach (string action in new[] { "retry_supplier", "credit_supplier_failure" })
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.ResolveAsync(actor, f.Context(0), order.Order, new(action, revision, "Public unresolved supplier", Guid.NewGuid(), request, requestRevision), Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.supplier_request_attempts WHERE outcome IN ('unknown','failed') AND error_code IS NULL", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='paid' AND resolution_kind IS NULL", Ct));
    }
    [Fact]
    public async Task PaidBankSupplierCreditRequiresExplicitUsdAndStaffNoticesNeverContainProtectedBonusContent()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, stock: 0, ct: Ct);
        var fixture = new PaymentSettlementFixture(f);
        await fixture.EnableBankAsync(Ct);
        await Supplier(f);
        var order = await fixture.BankOrderAsync(Ct);
        var actor = await f.AdminAsync(Ct);
        await fixture.Manual.SubmitAsync(f.Context(0), CheckoutFixture.Actor(0), order.Payment, "protected:synthetic/receipt", Ct);
        await fixture.Manual.DecideAsync(f.Context(0), actor, order.Payment, true, await AdminCatalogAndResolutionTests.Revision(f, order.Order), Guid.NewGuid(), "Public verified bank receipt", Ct);
        Guid request = await Fail(f, order.Order);
        var store = new OrderAdministrationStore(f.Sessions);
        long revision = await AdminCatalogAndResolutionTests.Revision(f, order.Order);
        var credit = new OrderResolution("credit_supplier_failure", revision, "Public staff credit", Guid.NewGuid(), request, await RequestRevision(f, request));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ResolveAsync(actor, f.Context(0), order.Order, credit, Ct));
        Guid audit = Guid.NewGuid();
        await using (var review = await f.Sessions.BeginAsync(Ct))
        { await AdminAuditStore.AppendAsync(review, actor, new(audit, AuditAction.Update, AuditEntity.SupplierRequest, request, f.Brands[0], AuditReason.OperatorReview, audit, SHA256.HashData("public review"u8)), Ct); await review.CommitAsync(Ct); }
        var notices = new CampaignStore(f.Sessions);
        Guid generation = Guid.NewGuid();
        Guid notice = await notices.StaffNoticeAsync(actor, f.Context(0), request, audit, generation, false, -10099, Ct);
        Assert.Equal(notice, await notices.StaffNoticeAsync(actor, f.Context(0), request, audit, generation, false, -10099, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => notices.StaffNoticeAsync(actor, f.Context(0), request, audit, generation, true, -10099, Ct));
        var lease = (await notices.ClaimNoticeAsync(Guid.NewGuid(), Ct))!;
        var dispatch = await notices.BeginNoticeAsync(f.Context(0), lease, TimeSpan.FromMilliseconds(100), -10099, new Dictionary<string, string>(), Ct);
        Assert.Equal("staff_chat", dispatch!.Recipient);
        Assert.Equal(-10099, dispatch.ChatId);
        Assert.DoesNotContain("protected:", dispatch.Text!, StringComparison.Ordinal);
        Assert.DoesNotContain("credential", dispatch.Text!, StringComparison.OrdinalIgnoreCase);
        await notices.RecordNoticeAsync(f.Context(0), lease, new(NoticeOutcomeKind.Sent, 123), Ct);
        await store.ResolveAsync(actor, f.Context(0), order.Order, credit with { BankCreditUsd = "7.25" }, Ct);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='supplier_wallet_credit' AND signed_amount=7.25", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.payments WHERE payment_method='bank' AND status='settled' AND amount_received=1000000", Ct));
    }
    private static async Task<Guid> Supplier(CheckoutFixture f)
    {
        Guid source = Guid.NewGuid();
        await using var edit = await f.Sessions.BeginAsync(Ct);
        await edit.ExecuteAsync(new DbStatement("INSERT INTO shop.product_supplier_sources(id,product_id,provider_code,external_product_id,enabled) VALUES ($1,$2,'synthetic','public product',true)", DbValue.Uuid(source), DbValue.Uuid(f.Product)), Ct);
        await edit.ExecuteAsync(new DbStatement("UPDATE shop.products SET fulfillment_kind='supplier',active_supplier_source_id=$1,revision=revision+1", DbValue.Uuid(source)), Ct);
        await edit.CommitAsync(Ct); return source;
    }
    private static async Task<Guid> Fail(CheckoutFixture f, Guid order)
    {
        await using var session = await f.Sessions.BeginAsync(Ct);
        Guid id = await session.QuerySingleAsync(new DbStatement("UPDATE shop.supplier_requests SET status='failed',review_code='provider_failed',revision=revision+1 WHERE order_id=$1 RETURNING id", DbValue.Uuid(order)), r => r.GetGuid(0), Ct);
        await session.ExecuteAsync(new DbStatement("UPDATE shop.orders SET fulfillment_status='requires_admin',review_code='provider_failed',revision=revision+1 WHERE id=$1", DbValue.Uuid(order)), Ct);
        await session.CommitAsync(Ct); return id;
    }
    private static async Task<long> RequestRevision(CheckoutFixture f, Guid request)
    { await using var read = await f.Sessions.BeginAsync(Ct); return await read.QuerySingleAsync(new DbStatement("SELECT revision FROM shop.supplier_requests WHERE id=$1", DbValue.Uuid(request)), r => r.GetInt64(0), Ct); }
}
