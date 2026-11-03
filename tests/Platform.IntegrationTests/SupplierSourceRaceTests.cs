using Platform.Core;
using Platform.Fulfillment;
using Platform.Persistence;
using Platform.Security;
using Platform.Telegram;
using Platform.Testing;

namespace Platform.IntegrationTests;

[Collection("PostgreSQL races")]
public sealed class SupplierSourceRaceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Lazy<string> Password = new(() => AdminAuth.CreatePasswordVerifier(new string('r', 20)));
    [Theory]
    [InlineData("wallet")]
    [InlineData("free")]
    [InlineData("crypto")]
    [InlineData("bank")]
    public async Task EveryAcceptanceRouteSerializesSelectionAndKeepsOneImmutableBindingAcrossHundredReplays(string method)
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        Guid original = await SupplierFixture.ConfigureAsync(f, Ct), next = Guid.NewGuid();
        var actor = await f.AdminAsync(Ct);
        var selector = await OtherAdminAsync(f);
        var store = new SupplierSourceSelectionStore(f.Sessions);
        var payment = new PaymentSettlementFixture(f);
        await store.CreateAsync(actor, f.Product, next, "synthetic", "Public next mapping", "Public next variant", Guid.NewGuid(), Ct);
        await store.SetEnabledAsync(actor, f.Product, next, true, 1, Guid.NewGuid(), payment.Catalog, Ct);
        Guid order, paymentId = Guid.Empty;
        string? origin = null;
        decimal due = 0;
        long revision = 0;
        if (method == "crypto")
        {
            await payment.EnableAsync(Ct);
            var invoice = await payment.OrderAsync(ct: Ct);
            order = invoice.Order; paymentId = invoice.Payment; origin = invoice.Origin; due = invoice.Due;
            await payment.CaptureAsync(paymentId, origin, due, ct: Ct);
        }
        else if (method == "bank")
        {
            await payment.EnableBankAsync(Ct);
            (paymentId, order) = await payment.BankOrderAsync(Ct);
            await payment.Manual.SubmitAsync(f.Context(0), CheckoutFixture.Actor(0), paymentId, "protected:synthetic/source-race", Ct);
            await using var read = await f.Sessions.BeginAsync(Ct);
            revision = await read.QuerySingleAsync(new DbStatement("SELECT revision FROM shop.orders WHERE id=$1", DbValue.Uuid(order)), r => r.GetInt64(0), Ct);
        }
        else
        {
            order = Guid.CreateVersion7();
            if (method == "free") await SupplierRecoveryTests.Coupon(f, 100);
        }
        var service = new WalletCheckoutService(f.Sessions, f.Store, payment.Catalog);
        var request = new CheckoutRequest(order, f.Product, 1);
        Guid decision = Guid.NewGuid();
        async Task AcceptAsync()
        {
            if (method == "crypto") await payment.Store.SettleAsync(f.Context(0), paymentId, PaymentSettlementFixture.Lookup, Ct);
            else if (method == "bank") await payment.Manual.DecideAsync(f.Context(0), actor, paymentId, true, revision, decision, "Public source race decision", Ct);
            else await service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), request, (_, _, _) => Task.FromResult(ChannelMembership.Member), Ct);
        }
        Task<Exception?> accepted = Record.ExceptionAsync(AcceptAsync).AsTask();
        await Task.WhenAll(accepted, store.SelectAsync(selector, f.Product, next, 1, 2, Guid.NewGuid(), Ct));
        Exception? failure = await accepted;
        if (failure is not null)
        {
            Assert.True(method is "wallet" or "free" && failure is PricingRejectedException or UnauthorizedAccessException);
            Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders", Ct));
            await AcceptAsync();
        }
        Guid bound;
        await using (var read = await f.Sessions.BeginAsync(Ct)) bound = await read.QuerySingleAsync(new DbStatement("SELECT product_supplier_source_id FROM shop.supplier_requests WHERE order_id=$1", DbValue.Uuid(order)), r => r.GetGuid(0), Ct);
        Assert.Contains(bound, new[] { original, next });
        for (int i = 0; i < 100; i++) await AcceptAsync();
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.supplier_requests WHERE product_supplier_source_id='" + bound.ToString("D") + "' AND account_ref='public account' AND cost_baseline_amount=1", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status IN ('paid','not_required')", Ct));
        Assert.Equal(method == "wallet" ? 1L : 0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit' AND signed_amount=-10", Ct));
        Assert.Equal(method == "free" ? 1L : 0L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='held' AND units_covered=1", Ct));
        Assert.Equal(method is "crypto" or "bank" ? 1L : 0L, await f.CountAsync("SELECT count(*) FROM shop.payments WHERE status='settled'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments", Ct));
    }
    [Fact]
    public async Task DistinctAdministratorsCannotBothSelectAtOneProductRevision()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        Guid original = await SupplierFixture.ConfigureAsync(f, Ct), next = await SupplierSourceSelectionQualification.AddAsync(f, Ct);
        var a = await f.AdminAsync(Ct);
        var b = await OtherAdminAsync(f);
        var store = new SupplierSourceSelectionStore(f.Sessions);
        var errors = await Task.WhenAll(new[] { (Actor: a, Source: original), (Actor: b, Source: next) }.Select(async item => await Record.ExceptionAsync(() => store.SelectAsync(item.Actor, f.Product, item.Source, 1, 1, Guid.NewGuid(), Ct))));
        Assert.Single(errors, error => error is null);
        Assert.Single(errors, error => error is InvalidOperationException);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.audit_events WHERE action='select_source'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='evaluate_restock'", Ct));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdapterReplacementReconcilesOnlyTheOriginalAccountAndNeverSubmitsAgain(bool differentAccount)
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await SupplierFixture.ConfigureAsync(f, Ct);
        await SupplierFixture.AcceptAsync(f, 1, Ct);
        var original = new SyntheticSupplier { Submit = _ => SyntheticSupplier.Unknown() };
        await new SupplierFulfillmentWorker(new(f.Sessions, f.Secrets), original, original).RunAsync(f.Context(0), await SupplierFixture.ClaimAsync(f, JobKind.SubmitSupplier, Ct), Ct);
        await using (var due = await f.Sessions.BeginAsync(Ct))
        { await due.ExecuteAsync(new DbStatement("UPDATE shop.jobs SET available_at=clock_timestamp() WHERE job_kind='reconcile_supplier' AND status='pending'"), Ct); await due.CommitAsync(Ct); }
        JobLease lease = await SupplierFixture.ClaimAsync(f, JobKind.ReconcileSupplier, Ct);
        var fresh = new SyntheticSupplier();
        var adapter = new AccountAdapter(fresh, differentAccount ? "public different account" : "public account");
        var worker = new SupplierFulfillmentWorker(new(f.Sessions, f.Secrets), adapter, fresh);
        if (differentAccount)
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => worker.RunAsync(f.Context(0), lease, Ct));
            Assert.Equal(0, adapter.Calls);
            Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.supplier_requests WHERE status='unknown' AND account_ref='public account'", Ct));
            Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments", Ct));
            await new SupplierFulfillmentWorker(new(f.Sessions, f.Secrets), fresh, fresh).RunAsync(f.Context(0), lease, Ct);
        }
        else await worker.RunAsync(f.Context(0), lease, Ct);
        Assert.Equal(1, original.Submits); Assert.Equal(0, fresh.Submits); Assert.Equal(1, fresh.Lookups);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.supplier_requests WHERE status='succeeded' AND account_ref='public account' AND source_revision=1 AND cost_baseline_amount=1", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.supplier_request_attempts WHERE operation='submit'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit' AND signed_amount=-10", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments WHERE status='acquired'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='supplier_wallet_credit'", Ct));
    }
    [Fact]
    public async Task ReferencedMappingIdentityCannotBeEditedOrDeletedAfterAcceptance()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        Guid source = await SupplierFixture.ConfigureAsync(f, Ct);
        await SupplierFixture.AcceptAsync(f, 1, Ct);
        foreach (string mutation in new[] { "provider_code='rapidx'", "external_product_id='public changed'", "external_variant_id='public changed'", "product_id=gen_random_uuid()" })
        {
            await using var invalid = await f.Sessions.BeginAsync(Ct);
            var error = await Assert.ThrowsAsync<DbOperationException>(() => invalid.ExecuteAsync(new DbStatement("UPDATE shop.product_supplier_sources SET " + mutation + " WHERE id=$1", DbValue.Uuid(source)), Ct));
            Assert.Equal(DbFailureKind.Constraint, error.Failure.Kind);
        }
        await using (var invalid = await f.Sessions.BeginAsync(Ct))
            Assert.Equal(DbFailureKind.Constraint, (await Assert.ThrowsAsync<DbOperationException>(() => invalid.ExecuteAsync(new DbStatement("DELETE FROM shop.product_supplier_sources WHERE id=$1", DbValue.Uuid(source)), Ct))).Failure.Kind);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.supplier_requests WHERE status='pending' AND source_revision=1 AND account_ref='public account' AND cost_baseline_amount=1", Ct));
    }
    private sealed class AccountAdapter(SyntheticSupplier inner, string account) : ISupplierAdapter
    {
        public string Provider => inner.Provider;
        public string Account => account;
        public bool ProvenIntentLookup => inner.ProvenIntentLookup;
        public int Calls { get; private set; }
        public Task<SupplierQuote> InspectAsync(SupplierBinding binding, CancellationToken ct) { Calls++; return inner.InspectAsync(binding, ct); }
        public Task<SupplierOutcome> SubmitAsync(SupplierBinding binding, decimal maximumUnitCharge, CancellationToken ct) { Calls++; return inner.SubmitAsync(binding, maximumUnitCharge, ct); }
        public Task<SupplierOutcome> LookupAsync(SupplierBinding binding, string? originalPurchaseReference, CancellationToken ct) { Calls++; return inner.LookupAsync(binding, originalPurchaseReference, ct); }
    }
    private static async Task<AuthenticatedAdmin> OtherAdminAsync(CheckoutFixture f)
    {
        await using (var seed = await f.Sessions.BeginAsync(Ct))
        {
            await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.admins(id,login,authentication_mode,password_verifier) VALUES (gen_random_uuid(),'public-source-selector','password',$1)", DbValue.Text(Password.Value)), Ct);
            await seed.CommitAsync(Ct);
        }
        await using var login = await f.Sessions.BeginAsync(Ct);
        using var ticket = await AdminSessionStore.LoginAsync(login, "public-source-selector", new string('r', 20).AsMemory(), TimeSpan.FromHours(1), Ct);
        await login.CommitAsync(Ct);
        await using var resolve = await f.Sessions.BeginAsync(Ct);
        return await AdminSessionStore.AuthenticateAsync(resolve, ticket.Token, Ct);
    }
}
