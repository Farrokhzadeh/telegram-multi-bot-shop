using Platform.Core;
using Platform.Fulfillment;
using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

[Collection("PostgreSQL races")]
public sealed class SupplierSourceAdministrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task CreationIsDisabledReplaySafeVariantUniqueAndBoundedWithoutIdentityEdits()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await SupplierFixture.ConfigureAsync(f, Ct);
        var actor = await f.AdminAsync(Ct);
        var store = new SupplierSourceSelectionStore(f.Sessions);
        Guid source = Guid.NewGuid(), command = Guid.NewGuid();
        for (int i = 0; i < 100; i++) Assert.Equal(1L, await store.CreateAsync(actor, f.Product, source, "synthetic", "Public mapping", null, command, Ct));
        await store.CreateAsync(actor, f.Product, Guid.NewGuid(), "synthetic", "Public mapping", "Public variant", Guid.NewGuid(), Ct);
        await Assert.ThrowsAsync<DbOperationException>(() => store.CreateAsync(actor, f.Product, Guid.NewGuid(), "synthetic", "Public mapping", null, Guid.NewGuid(), Ct));
        await Assert.ThrowsAsync<DbOperationException>(() => store.CreateAsync(actor, f.Product, source, "synthetic", "Public changed", null, command, Ct));
        var page = await store.SourcesAsync(actor, f.Product, null, 2, Ct);
        Assert.Equal(2, page.Items.Length); Assert.NotNull(page.Next);
        var tail = await store.SourcesAsync(actor, f.Product, page.Next, 2, Ct);
        Assert.Single(tail.Items); Assert.Null(tail.Next);
        var all = page.Items.Concat(tail.Items).ToArray();
        Assert.Equal(3, all.Select(x => x.Id).Distinct().Count());
        Assert.Single(all, x => x.Selected);
        Assert.Equal(2, all.Count(x => !x.Enabled && x.Revision == 1));
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.audit_events WHERE entity_kind='product_supplier_source' AND action='create'", Ct));
        Assert.Empty((await store.SourcesAsync(actor, Guid.NewGuid(), null, 2, Ct)).Items);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SourcesAsync(actor, f.Product, null, 101, Ct));
    }
    [Theory]
    [InlineData("ineligible")]
    [InlineData("error")]
    [InlineData("input")]
    [InlineData("empty_input")]
    [InlineData("cost")]
    [InlineData("account")]
    [InlineData("transport")]
    public async Task EnableRejectsUnprovenMappingsWithoutAuditObservationOrQueuedWork(string failure)
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await SupplierFixture.ConfigureAsync(f, Ct);
        var actor = await f.AdminAsync(Ct);
        var store = new SupplierSourceSelectionStore(f.Sessions);
        Guid source = Guid.NewGuid();
        await store.CreateAsync(actor, f.Product, source, "synthetic", "Public mapping", null, Guid.NewGuid(), Ct);
        var catalog = new CatalogStore(f.Sessions, f.Secrets, failure == "account" ? [] : [new("synthetic", "public account")], (_, _) => failure switch
        {
            "transport" => throw new IOException("Public synthetic transport failure"),
            "ineligible" => Task.FromResult(new SupplierCatalogResult(SupplierCatalogStatus.Ineligible, 0)),
            "error" => Task.FromResult(new SupplierCatalogResult(SupplierCatalogStatus.Error, 0)),
            "input" => Task.FromResult(new SupplierCatalogResult(SupplierCatalogStatus.Available, 1, 1, "USD", "public quote", false)),
            "empty_input" => Task.FromResult(new SupplierCatalogResult(SupplierCatalogStatus.OutOfStock, 0, 1, "USD", "public quote", false)),
            "cost" => Task.FromResult(new SupplierCatalogResult(SupplierCatalogStatus.Available, 1)),
            _ => throw new InvalidOperationException("Unconfigured provider must not be called.")
        });
        await Assert.ThrowsAsync<PricingRejectedException>(() => store.SetEnabledAsync(actor, f.Product, source, true, 1, Guid.NewGuid(), catalog, Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.product_supplier_sources WHERE NOT enabled AND revision=1 AND observation_revision=0", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.audit_events WHERE action='enable'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.jobs", Ct));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactTrustedInspectionEnablesOnceAndDisableRetainsSelectedIdentity(bool outOfStock)
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await SupplierFixture.ConfigureAsync(f, Ct);
        var actor = await f.AdminAsync(Ct);
        var store = new SupplierSourceSelectionStore(f.Sessions);
        Guid source = Guid.NewGuid(), command = Guid.NewGuid();
        await store.CreateAsync(actor, f.Product, source, "synthetic", "Public mapping", "Public variant", Guid.NewGuid(), Ct);
        int calls = 0;
        var catalog = new CatalogStore(f.Sessions, f.Secrets, [new("synthetic", "public account")], (request, _) =>
        {
            calls++; Assert.Equal("Public mapping", request.ProductRef); Assert.Equal("Public variant", request.VariantRef); Assert.Equal("public account", request.AccountRef); Assert.Equal(1, request.Quantity);
            return Task.FromResult(new SupplierCatalogResult(outOfStock ? SupplierCatalogStatus.OutOfStock : SupplierCatalogStatus.Available, outOfStock ? 0 : 10, 1, "USD", "public quote"));
        });
        Assert.Equal(2L, await store.SetEnabledAsync(actor, f.Product, source, true, 1, command, catalog, Ct));
        for (int i = 0; i < 100; i++) Assert.Equal(2L, await store.SetEnabledAsync(actor, f.Product, source, true, 1, command, null, Ct));
        Assert.Equal(1, calls);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.product_supplier_sources WHERE revision=2 AND observed_source_revision=2 AND observed_account_ref='public account' AND eligibility_code='ready_item'", Ct));
        await store.SelectAsync(actor, f.Product, source, 1, 2, Guid.NewGuid(), Ct);
        Guid disable = Guid.NewGuid();
        Assert.Equal(3L, await store.SetEnabledAsync(actor, f.Product, source, false, 2, disable, ct: Ct));
        Assert.Equal(3L, await store.SetEnabledAsync(actor, f.Product, source, false, 2, disable, ct: Ct));
        Assert.Single((await store.SourcesAsync(actor, f.Product, null, 100, Ct)).Items, x => x.Selected && !x.Enabled && x.Revision == 3);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SelectAsync(actor, f.Product, source, 2, 3, Guid.NewGuid(), Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.audit_events WHERE action='enable'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.audit_events WHERE action='disable'", Ct));
        Assert.Equal(CatalogAvailability.OutOfStock, (await catalog.GetAsync(f.Context(0), CheckoutFixture.Actor(0), f.Product, Ct))!.Availability);
        await Assert.ThrowsAsync<PricingRejectedException>(() => catalog.PrepareAsync(f.Context(0), CheckoutFixture.Actor(0), new(f.Product, 1, CheckoutPayment.Wallet), Ct));
        await f.AddStockAsync(1, Ct);
        Assert.Equal(0, (await catalog.PrepareAsync(f.Context(0), CheckoutFixture.Actor(0), new(f.Product, 1, CheckoutPayment.Wallet), Ct)).Supply.RemoteUnits);
        Assert.Equal(1, calls);
    }
    [Fact]
    public async Task RevocationDuringInspectionRejectsEnableAndMetadataWithoutKeepingProbeAudit()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await SupplierFixture.ConfigureAsync(f, Ct);
        var actor = await f.AdminAsync(Ct);
        var store = new SupplierSourceSelectionStore(f.Sessions);
        Guid source = Guid.NewGuid();
        await store.CreateAsync(actor, f.Product, source, "synthetic", "Public mapping", null, Guid.NewGuid(), Ct);
        var catalog = new CatalogStore(f.Sessions, f.Secrets, [new("synthetic", "public account")], async (_, ct) =>
        {
            await using var logout = await f.Sessions.BeginAsync(ct);
            await AdminSessionStore.LogoutAsync(logout, actor, ct); await logout.CommitAsync(ct);
            return new(SupplierCatalogStatus.Available, 10, 1, "USD", "public quote");
        });
        await Assert.ThrowsAsync<AdminAuthenticationException>(() => store.SetEnabledAsync(actor, f.Product, source, true, 1, Guid.NewGuid(), catalog, Ct));
        await Assert.ThrowsAsync<AdminAuthenticationException>(() => store.SourcesAsync(actor, f.Product, null, 10, Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.audit_events WHERE action='enable'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.product_supplier_sources WHERE NOT enabled AND revision=1", Ct));
    }
    [Fact]
    public async Task ProductChangesDuringInspectionRejectProofWithoutHoldingNetworkLocks()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await SupplierFixture.ConfigureAsync(f, Ct);
        var actor = await f.AdminAsync(Ct);
        var store = new SupplierSourceSelectionStore(f.Sessions);
        Guid source = Guid.NewGuid();
        await store.CreateAsync(actor, f.Product, source, "synthetic", "Public mapping", null, Guid.NewGuid(), Ct);
        var catalog = new CatalogStore(f.Sessions, f.Secrets, [new("synthetic", "public account")], async (_, ct) =>
        {
            await new AdminCatalogStore(f.Sessions, f.Secrets).ProductAsync(actor, f.Product, new("Public changed product", null, null, null, "supplier", "immediate", true, true), 1, Guid.NewGuid(), ct);
            return new(SupplierCatalogStatus.Available, 10, 1, "USD", "public quote");
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetEnabledAsync(actor, f.Product, source, true, 1, Guid.NewGuid(), catalog, Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.audit_events WHERE action='enable'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.product_supplier_sources WHERE NOT enabled AND revision=1 AND observation_revision=0", Ct));
    }
    [Fact]
    public async Task DeferredJobFailureRollsBackEnableObservationAndAuditThenOriginalCommandCanRetry()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await SupplierFixture.ConfigureAsync(f, Ct);
        var actor = await f.AdminAsync(Ct);
        var store = new SupplierSourceSelectionStore(f.Sessions);
        Guid source = Guid.NewGuid(), command = Guid.NewGuid();
        await store.CreateAsync(actor, f.Product, source, "synthetic", "Public mapping", null, Guid.NewGuid(), Ct);
        await using (var fault = f.Database.Data.CreateCommand("CREATE FUNCTION shop.reject_source_job() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'Public source fault' USING ERRCODE='23514'; END $$; CREATE CONSTRAINT TRIGGER reject_source_job AFTER INSERT ON shop.jobs DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (NEW.job_kind='evaluate_restock') EXECUTE FUNCTION shop.reject_source_job()")) await fault.ExecuteNonQueryAsync(Ct);
        var catalog = new PaymentSettlementFixture(f).Catalog;
        await Assert.ThrowsAsync<DbOperationException>(() => store.SetEnabledAsync(actor, f.Product, source, true, 1, command, catalog, Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.audit_events WHERE action='enable'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.product_supplier_sources WHERE NOT enabled AND revision=1 AND observation_revision=0", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.jobs", Ct));
        await using (var repair = f.Database.Data.CreateCommand("DROP TRIGGER reject_source_job ON shop.jobs; DROP FUNCTION shop.reject_source_job()")) await repair.ExecuteNonQueryAsync(Ct);
        Assert.Equal(2L, await store.SetEnabledAsync(actor, f.Product, source, true, 1, command, catalog, Ct));
    }
    [Fact]
    public async Task ReenablingSourceNeverClearsAcceptedReviewOrQueuesAnotherPurchase()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        Guid source = await SupplierFixture.ConfigureAsync(f, Ct);
        await SupplierFixture.AcceptAsync(f, 1, Ct);
        var actor = await f.AdminAsync(Ct);
        var store = new SupplierSourceSelectionStore(f.Sessions);
        await store.SetEnabledAsync(actor, f.Product, source, false, 1, Guid.NewGuid(), ct: Ct);
        var port = new SyntheticSupplier();
        await new SupplierFulfillmentWorker(new(f.Sessions, f.Secrets), port, port).RunAsync(f.Context(0), await SupplierFixture.ClaimAsync(f, JobKind.SubmitSupplier, Ct), Ct);
        await store.SetEnabledAsync(actor, f.Product, source, true, 2, Guid.NewGuid(), new PaymentSettlementFixture(f).Catalog, Ct);
        Assert.Equal(0, port.Submits);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.supplier_requests WHERE status='blocked' AND source_revision=1 AND review_code='source_disabled' AND cost_baseline_amount=1", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='paid' AND fulfillment_status='requires_admin' AND review_code='source_disabled'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='submit_supplier' AND status IN ('pending','retry')", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE signed_amount>0 AND movement_kind<>'admin_adjustment'", Ct));
    }
}
