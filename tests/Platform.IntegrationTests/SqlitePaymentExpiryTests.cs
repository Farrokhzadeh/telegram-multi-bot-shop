using Platform.Persistence;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class SqlitePaymentExpiryTests
{
    [Fact]
    public async Task ExpiryDistinguishesUnpaidProtectedAndNotDueOrdersWithoutLosingLiabilities()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("akanti-sqlite-expiry-");
        try
        {
            string path = Path.Combine(dir.FullName, "shop.db");
            await SqliteSchema.InitializeAsync(path, ct);
            var brand = CheckoutFixture.ResolveContexts()[0];
            var sessions = new SqliteSessionFactory(path);
            Guid product = Guid.NewGuid();
            Guid[] members = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
            Guid[] orders = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
            Guid[] payments = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
            DateTime old = DateTime.UtcNow.AddMinutes(-40);
            DateTime future = DateTime.UtcNow;
            await sessions.RunAsync(true, async (_, tx, token) =>
            {
                using var foundation = SqliteWallet.Command(tx, """
                    INSERT INTO brands(id,code,internal_name,public_name,market,default_language,bot_username,bot_id,
                        bot_token_ciphertext,bot_token_key_id,active)
                    VALUES ($brand,'stage','Stage','Stage','international','en','stage_bot',$bot,x'01','fixture',1);
                    INSERT INTO products(id,fulfillment_kind,availability_policy,title)
                    VALUES ($product,'manual','immediate','Synthetic goods');
                    """, ("$brand", brand.BrandId), ("$bot", brand.BotId), ("$product", product));
                await foundation.ExecuteNonQueryAsync(token);
                for (int i = 0; i < members.Length; i++)
                {
                    DateTime creation = i == 2 ? future : old;
                    using var insert = SqliteWallet.Command(tx, """
                        INSERT INTO users(id,brand_id,telegram_id,language)
                        VALUES ($member,$brand,$actor,'en');
                        INSERT INTO orders(id,user_id,product_id,quantity,order_kind,payment_status,
                            currency,payment_method,unit_price_usdt,auto_price_toman_snapshot,
                            pricing_rounding_step_snapshot,price_calculation_version,brand_public_name_snapshot,
                            product_title_snapshot,payment_terms_snapshot,fulfillment_kind_snapshot,local_first_snapshot,
                            is_preorder,created_at,updated_at,payment_due_at)
                        VALUES ($order,$member,$product,1,'product_purchase',$status,
                            'USDT','crypto','10.25',false,'5000','synthetic','Stage','Synthetic goods',
                            'synthetic terms','manual',false,false,$created,$created,$due);
                        INSERT INTO payments(id,order_id,payment_method,quote_currency,amount_due,
                            idempotency_ref,network_asset,status,first_payment_observed_at)
                        VALUES ($payment,$order,'crypto','USD','10.25',$origin,'USDTTRC20',$paymentStatus,$first);
                        """, ("$member", members[i]), ("$brand", brand.BrandId), ("$actor", 7001 + i),
                        ("$order", orders[i]), ("$product", product),
                        ("$status", i == 1 ? "confirmation_pending" : "awaiting_payment"),
                        ("$created", SqliteSchema.UtcText(creation)),
                        ("$due", SqliteSchema.UtcText(creation.AddMinutes(30))),
                        ("$payment", payments[i]), ("$origin", "order:" + orders[i].ToString("D")),
                        ("$paymentStatus", i == 1 ? "confirming" : "created"),
                        ("$first", i == 1 ? SqliteSchema.UtcText(old.AddMinutes(20)) : null));
                    await insert.ExecuteNonQueryAsync(token);
                    await OutboxStore.EnqueueSqliteAsync(tx,
                        JobIntent.Order(JobKind.ExpireOrderPayment, brand.BrandId, orders[i]),
                        orders[i], DateTime.UtcNow, token);
                }
                return 0;
            }, ct);
            var expiry = new SqlitePaymentExpiryStore(sessions);
            var observed = new Dictionary<Guid, SqlitePaymentExpiryOutcome>();
            for (int i = 0; i < 3; i++)
            {
                JobLease? lease = await expiry.ClaimAsync(brand, Guid.NewGuid(), ct);
                Assert.NotNull(lease);
                Guid origin = Assert.IsType<Guid>(lease.Intent.OriginId);
                observed.Add(origin, await expiry.RunAsync(brand, lease, ct));
            }
            Assert.Equal(SqlitePaymentExpiryOutcome.Expired, observed[orders[0]]);
            Assert.Equal(SqlitePaymentExpiryOutcome.Protected, observed[orders[1]]);
            Assert.Equal(SqlitePaymentExpiryOutcome.NotDue, observed[orders[2]]);
            await sessions.RunAsync(false, async (_, tx, token) =>
            {
                using var query = SqliteWallet.Command(tx, """
                    SELECT
                        (SELECT count(*) FROM orders WHERE id=$expired AND payment_status='expired'
                            AND fulfillment_status='cancelled' AND resolution_kind='unpaid_expiry'),
                        (SELECT count(*) FROM orders WHERE id=$protected
                            AND payment_status='confirmation_pending'),
                        (SELECT count(*) FROM orders WHERE id=$future AND payment_status='awaiting_payment'),
                        (SELECT count(*) FROM jobs WHERE job_kind='expire_order_payment' AND status='succeeded'),
                        (SELECT count(*) FROM jobs WHERE job_kind='expire_order_payment' AND status='retry'),
                        (SELECT count(*) FROM telegram_deliveries WHERE order_id=$expired),
                        (SELECT count(*) FROM wallet_entries)
                    """, ("$expired", orders[0]), ("$protected", orders[1]), ("$future", orders[2]));
                await using var row = await query.ExecuteReaderAsync(token);
                Assert.True(await row.ReadAsync(token));
                for (int i = 0; i < 3; i++) Assert.Equal(1, row.GetInt64(i));
                Assert.Equal(2, row.GetInt64(3));
                Assert.Equal(1, row.GetInt64(4));
                Assert.Equal(1, row.GetInt64(5));
                Assert.Equal(0, row.GetInt64(6));
                return 0;
            }, ct);
        }
        finally { dir.Delete(recursive: true); }
    }
}
