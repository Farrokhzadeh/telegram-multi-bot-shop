using Platform.Persistence;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class SupplierSchemaTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task AcceptedRemainderBindingAndAttemptCeilingAreImmutableAndRequireDurableIntent()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 3, ct: Ct);
        await SupplierFixture.ConfigureAsync(f, Ct);
        Guid order = await SupplierFixture.AcceptAsync(f, 5, Ct);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.supplier_requests WHERE requested_quantity=2 AND cost_baseline_amount=1 AND cost_currency='USD'", Ct));
        Assert.Equal(3L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments WHERE kind='local' AND status='acquired'", Ct));
        foreach (string mutation in new[] { "requested_quantity=1", "cost_baseline_amount=2", "account_ref='changed'", "local_intent_key='changed'", "source_revision=2" })
        {
            await using var invalid = await f.Sessions.BeginAsync(Ct);
            var error = await Assert.ThrowsAsync<DbOperationException>(() => invalid.ExecuteAsync(new DbStatement("UPDATE shop.supplier_requests SET " + mutation + " WHERE order_id=$1", DbValue.Uuid(order)), Ct));
            Assert.Equal(DbFailureKind.Constraint, error.Failure.Kind);
        }
        await using (var invalid = await f.Sessions.BeginAsync(Ct))
            await Assert.ThrowsAsync<DbOperationException>(() => invalid.ExecuteAsync(new DbStatement("INSERT INTO shop.supplier_request_attempts(id,supplier_request_id,attempt_no,operation,max_charge_amount) SELECT gen_random_uuid(),id,1,'submit',1.10000001 FROM shop.supplier_requests"), Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.supplier_request_attempts", Ct));
        Assert.Equal(28L, await f.CountAsync("SELECT count(*) FROM pg_tables WHERE schemaname='shop'", Ct));
    }
}
