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
	[Fact]
	public async Task ProductActiveSupplierSourceMustBelongToSameProduct()
	{
		await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
		Guid source = await SupplierFixture.ConfigureAsync(f, Ct);
		Guid foreignProduct = Guid.NewGuid();
		await using (var seed = await f.Sessions.BeginAsync(Ct))
		{
			await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.products(id,fulfillment_kind,availability_policy,title) VALUES ($1,'supplier','immediate','foreign product')", DbValue.Uuid(foreignProduct)), Ct);
			await seed.CommitAsync(Ct);
		}
		await using var invalid = await f.Sessions.BeginAsync(Ct);
		var err = await Assert.ThrowsAsync<DbOperationException>(() => invalid.ExecuteAsync(new DbStatement("UPDATE shop.products SET active_supplier_source_id=$1 WHERE id=$2", DbValue.Uuid(source), DbValue.Uuid(foreignProduct)), Ct));
		Assert.Equal(DbFailureKind.Constraint, err.Failure.Kind);
	}
	[Fact]
	public async Task SelectedSourceMustBeEnabledAtSelection()
	{
		await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
		Guid disabledSource = Guid.NewGuid();
		await using (var seed = await f.Sessions.BeginAsync(Ct))
		{
			await seed.ExecuteAsync(new DbStatement("""
				INSERT INTO shop.product_supplier_sources(id,product_id,provider_code,external_product_id,enabled)
				VALUES ($1,$2,'synthetic','ext-disabled',false)
				""", DbValue.Uuid(disabledSource), DbValue.Uuid(f.Product)), Ct);
			await seed.CommitAsync(Ct);
		}
		await using var invalid = await f.Sessions.BeginAsync(Ct);
		var err = await Assert.ThrowsAsync<DbOperationException>(() => invalid.ExecuteAsync(new DbStatement("UPDATE shop.products SET active_supplier_source_id=$1 WHERE id=$2", DbValue.Uuid(disabledSource), DbValue.Uuid(f.Product)), Ct));
		Assert.Equal(DbFailureKind.Constraint, err.Failure.Kind);
	}
	[Fact]
	public async Task NullAwareSourceMappingUniquenessRejectsDuplicateProviderExternalProduct()
	{
		await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
		Guid s1 = Guid.NewGuid(), s2 = Guid.NewGuid();
		await using (var valid = await f.Sessions.BeginAsync(Ct))
		{
			await valid.ExecuteAsync(new DbStatement("""
				INSERT INTO shop.product_supplier_sources(id,product_id,provider_code,external_product_id,external_variant_id,enabled)
				VALUES ($1,$2,'synthetic','ext-1',NULL,true)
				""", DbValue.Uuid(s1), DbValue.Uuid(f.Product)), Ct);
			await valid.CommitAsync(Ct);
		}
		await using (var invalid = await f.Sessions.BeginAsync(Ct))
		{
			var err = await Assert.ThrowsAsync<DbOperationException>(() => invalid.ExecuteAsync(new DbStatement("""
				INSERT INTO shop.product_supplier_sources(id,product_id,provider_code,external_product_id,external_variant_id,enabled)
				VALUES ($1,$2,'synthetic','ext-1',NULL,true)
				""", DbValue.Uuid(s2), DbValue.Uuid(f.Product)), Ct));
			Assert.Equal(DbFailureKind.Constraint, err.Failure.Kind);
		}
		Guid sVar1 = Guid.NewGuid(), sVar2 = Guid.NewGuid();
		await using (var validVar = await f.Sessions.BeginAsync(Ct))
		{
			await validVar.ExecuteAsync(new DbStatement("""
				INSERT INTO shop.product_supplier_sources(id,product_id,provider_code,external_product_id,external_variant_id,enabled)
				VALUES ($1,$2,'synthetic','ext-1','v1',true)
				""", DbValue.Uuid(sVar1), DbValue.Uuid(f.Product)), Ct);
			await validVar.CommitAsync(Ct);
		}
		await using (var invalidVar = await f.Sessions.BeginAsync(Ct))
		{
			var err = await Assert.ThrowsAsync<DbOperationException>(() => invalidVar.ExecuteAsync(new DbStatement("""
				INSERT INTO shop.product_supplier_sources(id,product_id,provider_code,external_product_id,external_variant_id,enabled)
				VALUES ($1,$2,'synthetic','ext-1','v1',true)
				""", DbValue.Uuid(sVar2), DbValue.Uuid(f.Product)), Ct));
			Assert.Equal(DbFailureKind.Constraint, err.Failure.Kind);
		}
		Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.product_supplier_sources WHERE external_product_id='ext-1'", Ct));
	}
	[Fact]
	public async Task ProductSupplierSourcesCannotBeTruncated()
	{
		await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
		await using var invalid = await f.Sessions.BeginAsync(Ct);
		var err = await Assert.ThrowsAsync<DbOperationException>(() => invalid.ExecuteAsync(new DbStatement("TRUNCATE shop.product_supplier_sources"), Ct));
		Assert.Equal(DbFailureKind.Constraint, err.Failure.Kind);
	}
}
