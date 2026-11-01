using Platform.Core;
using Platform.Persistence;
using Platform.Telegram;

namespace Platform.Testing;

public static class SupplierFixture
{
    public static async Task<Guid> ConfigureAsync(CheckoutFixture f, CancellationToken ct)
    {
        Guid source = Guid.NewGuid();
        await using var session = await f.Sessions.BeginAsync(ct);
        await session.ExecuteAsync(new DbStatement("INSERT INTO shop.product_supplier_sources(id,product_id,provider_code,external_product_id,enabled) VALUES ($1,$2,'synthetic','public product',true)", DbValue.Uuid(source), DbValue.Uuid(f.Product)), ct);
        await session.ExecuteAsync(new DbStatement("UPDATE shop.products SET fulfillment_kind='supplier',active_supplier_source_id=$1,revision=revision+1", DbValue.Uuid(source)), ct);
        await session.CommitAsync(ct); return source;
    }
    public static async Task<Guid> AcceptAsync(CheckoutFixture f, int quantity, CancellationToken ct)
    {
        var catalog = new PaymentSettlementFixture(f).Catalog;
        var result = await new WalletCheckoutService(f.Sessions, f.Store, catalog).PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), new(Guid.CreateVersion7(), f.Product, quantity),
            (_, _, _) => Task.FromResult(ChannelMembership.Member), ct);
        if (result.Outcome != CheckoutOutcome.Purchased) throw new InvalidOperationException("Synthetic supplier acceptance failed.");
        return result.OrderId!.Value;
    }
    public static async Task<JobLease> ClaimAsync(CheckoutFixture f, JobKind kind, CancellationToken ct)
    {
        await using var session = await f.Sessions.BeginAsync(ct);
        var lease = await OutboxStore.ClaimAsync(session, kind, Guid.NewGuid(), TimeSpan.FromMinutes(1), ct) ?? throw new InvalidOperationException("Synthetic supplier work missing.");
        await session.CommitAsync(ct); return lease;
    }
}
