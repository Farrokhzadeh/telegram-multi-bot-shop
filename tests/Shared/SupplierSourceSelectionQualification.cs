using Platform.Core;
using Platform.Persistence;
using Platform.Telegram;

namespace Platform.Testing;

public static class SupplierSourceSelectionQualification
{
    public static async Task RunAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: ct);
        Guid original = await SupplierFixture.ConfigureAsync(f, ct), next = Guid.NewGuid();
        Guid oldOrder = await SupplierFixture.AcceptAsync(f, 1, ct);
        var admin = await f.AdminAsync(ct);
        var selection = new SupplierSourceSelectionStore(f.Sessions);
        await selection.CreateAsync(admin, f.Product, next, "synthetic", "public next product", "public next variant", Guid.NewGuid(), ct);
        await selection.SetEnabledAsync(admin, f.Product, next, true, 1, Guid.NewGuid(), new PaymentSettlementFixture(f).Catalog, ct);
        Guid command = Guid.NewGuid();
        if (await selection.SelectAsync(admin, f.Product, next, 1, 2, command, ct) != 2) throw new InvalidOperationException("Source selection revision failed.");
        var catalog = new PaymentSettlementFixture(f).Catalog;
        var service = new WalletCheckoutService(f.Sessions, f.Store, catalog);
        for (int user = 0; user < 3; user++)
        {
            var result = await service.PurchaseAsync(f.Context(user), CheckoutFixture.Actor(user), new(Guid.CreateVersion7(), f.Product, 1), (_, _, _) => Task.FromResult(ChannelMembership.Member), ct);
            if (result.Outcome != CheckoutOutcome.Purchased) throw new InvalidOperationException("Shared supplier selection failed.");
        }
        await selection.SelectAsync(admin, f.Product, original, 2, 1, Guid.NewGuid(), ct);
        if (await selection.SelectAsync(admin, f.Product, next, 1, 2, command, ct) != 2 ||
            await f.CountAsync("SELECT count(*) FROM shop.products WHERE revision=3 AND active_supplier_source_id='" + original.ToString("D") + "'", ct) != 1 ||
            await f.CountAsync("SELECT count(*) FROM shop.supplier_requests WHERE order_id='" + oldOrder.ToString("D") + "' AND product_supplier_source_id='" + original.ToString("D") + "' AND source_revision=1", ct) != 1 ||
            await f.CountAsync("SELECT count(DISTINCT u.brand_id) FROM shop.supplier_requests r JOIN shop.orders o ON o.id=r.order_id JOIN shop.users u ON u.id=o.user_id WHERE r.product_supplier_source_id='" + next.ToString("D") + "' AND r.source_revision=2 AND r.external_variant_id='public next variant' AND r.account_ref='public account'", ct) != 3 ||
            await f.CountAsync("SELECT count(*) FROM shop.audit_events WHERE action='select_source' AND entity_kind='product' AND brand_id IS NULL AND command_sha256 IS NOT NULL", ct) != 2)
            throw new InvalidOperationException("Source selection changed original bindings or replayed writes.");
    }
    public static async Task<Guid> AddAsync(CheckoutFixture f, CancellationToken ct = default, bool enabled = true, Guid? product = null)
    {
        Guid id = Guid.NewGuid();
        await using var session = await f.Sessions.BeginAsync(ct);
        await session.ExecuteAsync(new DbStatement("INSERT INTO shop.product_supplier_sources(id,product_id,provider_code,external_product_id,external_variant_id,enabled) VALUES ($1,$2,'synthetic','public next product','public next variant',$3)", DbValue.Uuid(id), DbValue.Uuid(product ?? f.Product), DbValue.Boolean(enabled)), ct);
        await session.CommitAsync(ct); return id;
    }
}
