using Platform.Persistence;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class SqliteCustomerScreensTests
{
    [Fact]
    public async Task CustomerHomeAndPagesStayWithinBrandAndSurviveReopen()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("akanti-screen-");
        try
        {
            string path = Path.Combine(directory.FullName, "shop.db");
            await SqliteSchema.InitializeAsync(path, ct);
            var sessions = new SqliteSessionFactory(path);
            var contexts = CheckoutFixture.ResolveContexts();
            Guid first = Guid.NewGuid(), second = Guid.NewGuid(), product = Guid.NewGuid();
            await sessions.RunAsync(true, async (_, tx, token) =>
            {
                using var command = SqliteWallet.Command(tx, """
                    INSERT INTO brands(id,code,internal_name,public_name,market,default_language,bot_username,bot_id,
                        bot_token_ciphertext,bot_token_key_id,active)
                    VALUES ($brandA,'first','First','First','international','en','first_bot',1001,x'01','fixture',1);
                    INSERT INTO brands(id,code,internal_name,public_name,market,default_language,bot_username,bot_id,
                        bot_token_ciphertext,bot_token_key_id,active)
                    VALUES ($brandB,'second','Second','Second','international','en','second_bot',1002,x'01','fixture',1);
                    INSERT INTO users(id,brand_id,telegram_id,language) VALUES ($first,$brandA,7001,'en');
                    INSERT INTO users(id,brand_id,telegram_id,language) VALUES ($second,$brandB,7001,'fa');
                    INSERT INTO wallets(user_id) VALUES ($first),($second);
                    INSERT INTO products(id,fulfillment_kind,availability_policy,title) VALUES ($product,'local','immediate','Fixture');
                    """, ("$brandA", contexts[0].BrandId), ("$brandB", contexts[1].BrandId), ("$first", first),
                    ("$second", second), ("$product", product));
                await command.ExecuteNonQueryAsync(token);
                return true;
            }, ct);
            var firstOrder = Guid.NewGuid();
            await sessions.RunAsync(true, async (_, tx, token) =>
            {
                for (int i = 0; i < 8; i++)
                {
                    using var order = SqliteWallet.Command(tx, """
                        INSERT INTO orders(id,user_id,product_id,quantity,order_kind,payment_status,currency,payment_method,unit_price_usdt,
                            auto_price_toman_snapshot,pricing_rounding_step_snapshot,price_calculation_version,
                            brand_public_name_snapshot,product_title_snapshot,payment_terms_snapshot,fulfillment_kind_snapshot,local_first_snapshot,is_preorder,payment_confirmed_at)
                        VALUES ($id,$member,$product,1,'product_purchase','paid','USDT','wallet','10',false,'5000',
                            'synthetic','Fixture','Fixture','wallet','local',true,false,$now)
                        """, ("$id", i == 0 ? firstOrder : Guid.NewGuid()), ("$member", i == 7 ? second : first),
                        ("$product", product), ("$now", SqliteSchema.UtcText(DateTime.UtcNow)));
                    await order.ExecuteNonQueryAsync(token);
                }
                return true;
            }, ct);
            var screen = new SqliteCustomerScreens(new SqliteSessionFactory(path));
            var home = await screen.HomeAsync(contexts[0], 7001, ct);
            Assert.Equal("First", home.Brand);
            Assert.Equal(7, home.Orders);
            Assert.Equal(0, home.Balance);
            var page0 = await screen.ListAsync(contexts[0], 7001, CustomerScreenList.Orders, "all", 0, ct);
            var page1 = await screen.ListAsync(contexts[0], 7001, CustomerScreenList.Orders, "all", 1, ct);
            Assert.Equal(5, page0.Entries.Count);
            Assert.True(page0.HasNext);
            Assert.Equal(2, page1.Entries.Count);
            Assert.False(page1.HasNext);
            Assert.NotNull(await screen.DetailAsync(contexts[0], 7001, CustomerScreenList.Orders, firstOrder, ct));
            Assert.Null(await screen.DetailAsync(contexts[1], 7001, CustomerScreenList.Orders, firstOrder, ct));
            await screen.SetLanguageAsync(contexts[0], 7001, "fa", ct);
            Assert.Equal("fa", (await screen.HomeAsync(contexts[0], 7001, ct)).Language);
            Assert.Equal("fa", (await screen.HomeAsync(contexts[1], 7001, ct)).Language);
        }
        finally { directory.Delete(recursive: true); }
    }
}
