using Platform.Persistence;

namespace Platform.IntegrationTests;

public sealed class CommerceSchemaTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CommerceRepresentationKeepsMembershipIdentityAndExactTypedMoney()
    {
        await using var fixture = await BrandFixture.CreateAsync(Ct);
        await using var read = await fixture.Sessions.BeginAsync(Ct);
        Assert.Equal(0L, await read.QuerySingleAsync(new DbStatement("""
            SELECT count(*) FROM information_schema.columns WHERE table_schema='shop' AND
                ((table_name IN ('wallets','wallet_entries') AND column_name='currency') OR
                 (table_name='orders' AND column_name IN ('brand_id','telegram_id','public_ref','total_usdt','total_toman','payable_usd','pricing_mode')) OR
                 data_type IN ('json','jsonb','ARRAY','real','double precision'))
            """), row => row.GetInt64(0), Ct));
        Assert.Equal("user_id", await read.QuerySingleAsync(new DbStatement("""
            SELECT a.attname::text FROM pg_index i JOIN pg_attribute a ON a.attrelid=i.indrelid AND a.attnum=ANY(i.indkey)
            WHERE i.indrelid='shop.wallets'::regclass AND i.indisprimary
            """), row => row.GetString(0), Ct));
        Assert.Equal(28L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM pg_tables WHERE schemaname='shop'"), row => row.GetInt64(0), Ct));
    }

    [Fact]
    public async Task ListingsEnforceFieldSpecificInheritOverrideAndClear()
    {
        await using var fixture = await BrandFixture.CreateAsync(Ct);
        await using (var edit = await fixture.Sessions.BeginAsync(Ct))
        {
            await edit.ExecuteAsync(new DbStatement("""
                UPDATE shop.brand_products SET title_mode='override',title_override='public brand title',description_mode='clear',
                    icon_mode='override',icon_override='public-brand-icon',guidance_mode='inherit',auto_price_toman=false,price_toman=12345
                WHERE brand_id=$1 AND product_id=$2
                """, DbValue.Uuid(fixture.Brands[0]), DbValue.Uuid(fixture.Product)), Ct);
            await edit.CommitAsync(Ct);
        }
        foreach (string mutation in new[] { "title_mode='clear',title_override=NULL", "description_mode='clear',description_override='forbidden'", "icon_mode='override',icon_override=NULL", "guidance_mode='inherit',guidance_override='forbidden'", "allow_bank=true,auto_price_toman=false,price_toman=NULL" })
        {
            await using var invalid = await fixture.Sessions.BeginAsync(Ct);
            var error = await Assert.ThrowsAsync<DbOperationException>(() => invalid.ExecuteAsync(new DbStatement("UPDATE shop.brand_products SET " + mutation + " WHERE brand_id=$1 AND product_id=$2", DbValue.Uuid(fixture.Brands[0]), DbValue.Uuid(fixture.Product)), Ct));
            Assert.Equal(DbSessionState.Aborted, invalid.State);
            Assert.NotNull(error);
        }
    }

    [Fact]
    public async Task NewOrderCannotBorrowForeignBrandListingAndHasOnePublicUuid()
    {
        await using var fixture = await BrandFixture.CreateAsync(Ct);
        await using var invalid = await fixture.Sessions.BeginAsync(Ct);
        await Assert.ThrowsAsync<DbOperationException>(() => invalid.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.orders(id,user_id,product_id,quantity,order_kind,payment_status,currency,payment_method,unit_price_usdt,
                auto_price_toman_snapshot,pricing_rounding_step_snapshot,price_calculation_version,brand_public_name_snapshot,
                product_title_snapshot,payment_terms_snapshot,fulfillment_kind_snapshot,local_first_snapshot,is_preorder,payment_due_at)
            SELECT $1,$2,$3,quantity,order_kind,payment_status,currency,payment_method,unit_price_usdt,auto_price_toman_snapshot,
                pricing_rounding_step_snapshot,price_calculation_version,brand_public_name_snapshot,product_title_snapshot,
                payment_terms_snapshot,fulfillment_kind_snapshot,local_first_snapshot,is_preorder,payment_due_at FROM shop.orders WHERE id=$4
            """, DbValue.Uuid(Guid.NewGuid()), DbValue.Uuid(fixture.OtherUser), DbValue.Uuid(fixture.PrivateProduct), DbValue.Uuid(fixture.Orders[0])), Ct));
        await using var read = await fixture.Sessions.BeginAsync(Ct);
        Assert.Equal(3L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.orders"), row => row.GetInt64(0), Ct));
    }

    [Fact]
    public async Task OrderSnapshotsSurviveCatalogEditsAndCannotBeRequotedOrSwitchPayment()
    {
        await using var fixture = await BrandFixture.CreateAsync(Ct);
        await using (var edit = await fixture.Sessions.BeginAsync(Ct))
        {
            await edit.ExecuteAsync(new DbStatement("UPDATE shop.brand_products SET price_usdt=123.4567890123456789,visible=false WHERE brand_id=$1 AND product_id=$2", DbValue.Uuid(fixture.Brands[0]), DbValue.Uuid(fixture.Product)), Ct);
            await edit.ExecuteAsync(new DbStatement("UPDATE shop.products SET title='changed product',local_first=false WHERE id=$1", DbValue.Uuid(fixture.Product)), Ct);
            await edit.ExecuteAsync(new DbStatement("UPDATE shop.brands SET public_name='changed brand' WHERE id=$1", DbValue.Uuid(fixture.Brands[0])), Ct);
            await edit.CommitAsync(Ct);
        }
        foreach (string mutation in new[] { "quantity=2", "unit_price_usdt=11", "currency='USD'", "payment_method='wallet'", "brand_public_name_snapshot='changed brand'", "product_title_snapshot='changed product'", "local_first_snapshot=false", "payment_due_at=payment_due_at+INTERVAL '1 hour'", "price_calculation_version='changed'" })
        {
            await using var invalid = await fixture.Sessions.BeginAsync(Ct);
            await Assert.ThrowsAsync<DbOperationException>(() => invalid.ExecuteAsync(new DbStatement("UPDATE shop.orders SET " + mutation + " WHERE id=$1", DbValue.Uuid(fixture.Orders[0])), Ct));
        }
        await using var read = await fixture.Sessions.BeginAsync(Ct);
        Assert.Equal(("10", "public brand", "public product", true, "USDT", "crypto"), await read.QuerySingleAsync(new DbStatement("SELECT unit_price_usdt::text,brand_public_name_snapshot,product_title_snapshot,local_first_snapshot,currency,payment_method FROM shop.orders WHERE id=$1", DbValue.Uuid(fixture.Orders[0])),
            row => (row.GetString(0), row.GetString(1), row.GetString(2), row.GetBoolean(3), row.GetString(4), row.GetString(5)), Ct));
    }
}
