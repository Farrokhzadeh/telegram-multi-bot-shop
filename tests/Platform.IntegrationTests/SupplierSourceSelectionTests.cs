using Platform.Core;
using Platform.Persistence;
using Platform.Security;
using Platform.Telegram;
using Platform.Testing;

namespace Platform.IntegrationTests;

[Collection("PostgreSQL races")]
public sealed class SupplierSourceSelectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task NativeCompatibleSharedSelectionPreservesPaidBindingsAndOriginalReplayResult() => await SupplierSourceSelectionQualification.RunAsync(Ct);
    [Fact]
    public async Task HundredConcurrentSelectionReplaysCommitOneAuditRevisionAndRestockJob()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await SupplierFixture.ConfigureAsync(f, Ct);
        Guid next = await SupplierSourceSelectionQualification.AddAsync(f, Ct), command = Guid.NewGuid();
        var admin = await f.AdminAsync(Ct);
        var store = new SupplierSourceSelectionStore(f.Sessions);
        using var limit = new SemaphoreSlim(8);
        var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(async _ =>
        {
            await limit.WaitAsync(Ct);
            try { return await store.SelectAsync(admin, f.Product, next, 1, 1, command, Ct); }
            finally { limit.Release(); }
        }));
        Assert.All(results, revision => Assert.Equal(2L, revision));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.audit_events WHERE action='select_source' AND previous_revision=1 AND new_revision=2 AND brand_id IS NULL", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='evaluate_restock'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.products WHERE revision=2", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders", Ct));
        var error = await Assert.ThrowsAsync<DbOperationException>(() => store.SelectAsync(admin, f.Product, next, 1, 2, command, Ct));
        Assert.Equal(DbFailureKind.InvalidData, error.Failure.Kind);
    }
    [Theory]
    [InlineData("disabled")]
    [InlineData("foreign")]
    [InlineData("source_revision")]
    [InlineData("product_revision")]
    [InlineData("local")]
    public async Task InvalidSelectionRollsBackAuditProductAndOutbox(string invalid)
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        Guid original = await SupplierFixture.ConfigureAsync(f, Ct), product = f.Product;
        if (invalid == "foreign")
        {
            product = Guid.NewGuid();
            await using var seed = await f.Sessions.BeginAsync(Ct);
            await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.products(id,title,fulfillment_kind) VALUES ($1,'Public other product','supplier')", DbValue.Uuid(product)), Ct);
            await seed.CommitAsync(Ct);
        }
        Guid next = await SupplierSourceSelectionQualification.AddAsync(f, Ct, invalid != "disabled", product);
        if (invalid == "local")
        {
            await using var edit = await f.Sessions.BeginAsync(Ct);
            await edit.ExecuteAsync(new DbStatement("UPDATE shop.products SET fulfillment_kind='local'"), Ct); await edit.CommitAsync(Ct);
        }
        var store = new SupplierSourceSelectionStore(f.Sessions);
        var admin = await f.AdminAsync(Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SelectAsync(admin, f.Product, next, invalid == "product_revision" ? 0 : 1, invalid == "source_revision" ? 2 : 1, Guid.NewGuid(), Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.products WHERE active_supplier_source_id='" + original.ToString("D") + "' AND revision=1", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.audit_events WHERE action='select_source'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.jobs", Ct));
    }
    [Fact]
    public async Task CompetingCommandsAtOneProductRevisionHaveOneWinnerAndNoOrphanAudit()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        Guid original = await SupplierFixture.ConfigureAsync(f, Ct), next = await SupplierSourceSelectionQualification.AddAsync(f, Ct);
        var admin = await f.AdminAsync(Ct);
        var store = new SupplierSourceSelectionStore(f.Sessions);
        var errors = await Task.WhenAll(new[] { original, next }.Select(async source => await Record.ExceptionAsync(() => store.SelectAsync(admin, f.Product, source, 1, 1, Guid.NewGuid(), Ct))));
        Assert.Single(errors, error => error is null);
        Assert.Single(errors, error => error is InvalidOperationException);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.audit_events WHERE action='select_source'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.products WHERE revision=2", Ct));
    }
    [Fact]
    public async Task RevokedAdministratorCannotReplayAnEarlierSelection()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await SupplierFixture.ConfigureAsync(f, Ct);
        Guid next = await SupplierSourceSelectionQualification.AddAsync(f, Ct), command = Guid.NewGuid();
        var admin = await f.AdminAsync(Ct);
        var store = new SupplierSourceSelectionStore(f.Sessions);
        await store.SelectAsync(admin, f.Product, next, 1, 1, command, Ct);
        await using (var logout = await f.Sessions.BeginAsync(Ct))
        { await AdminSessionStore.LogoutAsync(logout, admin, Ct); await logout.CommitAsync(Ct); }
        await Assert.ThrowsAsync<AdminAuthenticationException>(() => store.SelectAsync(admin, f.Product, next, 1, 1, command, Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.audit_events WHERE action='select_source'", Ct));
    }
    [Fact]
    public async Task AuditedSwitchDuringDirectRefreshRejectsOldEvidenceWithoutFinancialMutation()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await SupplierFixture.ConfigureAsync(f, Ct);
        Guid next = await SupplierSourceSelectionQualification.AddAsync(f, Ct);
        var admin = await f.AdminAsync(Ct);
        var catalog = new CatalogStore(f.Sessions, f.Secrets, [new("synthetic", "public account")], async (_, ct) =>
        {
            await new SupplierSourceSelectionStore(f.Sessions).SelectAsync(admin, f.Product, next, 1, 1, Guid.NewGuid(), ct);
            return new(SupplierCatalogStatus.Available, 10, 1, "USD", "public old quote");
        });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => catalog.PrepareAsync(f.Context(0), CheckoutFixture.Actor(0), new(f.Product, 1, CheckoutPayment.Wallet), Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind<>'admin_adjustment'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.product_supplier_sources WHERE observation_revision>0", Ct));
    }
    [Fact]
    public async Task UnpaidInvoiceBindsSelectedSourceAtSettlementAndNeverChangesOnReplay()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        Guid original = await SupplierFixture.ConfigureAsync(f, Ct), next = await SupplierSourceSelectionQualification.AddAsync(f, Ct);
        var fixture = new PaymentSettlementFixture(f);
        await fixture.EnableAsync(Ct);
        var invoice = await fixture.OrderAsync(ct: Ct);
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.supplier_requests", Ct));
        var admin = await f.AdminAsync(Ct);
        var store = new SupplierSourceSelectionStore(f.Sessions);
        await store.SelectAsync(admin, f.Product, next, 1, 1, Guid.NewGuid(), Ct);
        Assert.NotNull(await fixture.Catalog.GetAsync(f.Context(0), CheckoutFixture.Actor(0), f.Product, Ct));
        await fixture.CaptureAsync(invoice.Payment, invoice.Origin, invoice.Due, ct: Ct);
        Assert.Equal(PaymentSettlementOutcome.Settled, (await fixture.Store.SettleAsync(f.Context(0), invoice.Payment, PaymentSettlementFixture.Lookup, Ct)).Outcome);
        await store.SelectAsync(admin, f.Product, original, 2, 1, Guid.NewGuid(), Ct);
        Assert.Equal(PaymentSettlementOutcome.Replayed, (await fixture.Store.SettleAsync(f.Context(0), invoice.Payment, (_, _) => throw new InvalidOperationException(), Ct)).Outcome);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.supplier_requests WHERE product_supplier_source_id='" + next.ToString("D") + "' AND source_revision=1 AND external_variant_id='public next variant'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.payments WHERE status='settled'", Ct));
    }
}
