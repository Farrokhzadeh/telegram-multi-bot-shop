using System.Security.Cryptography;
using System.Text;
using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class SqliteDeliveryTests
{
    [Fact]
    public async Task FrozenManifestAndTelegramLeaseCompleteOnceAfterReopen()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("akanti-delivery-");
        try
        {
            string path = Path.Combine(directory.FullName,"shop.db");
            await SqliteSchema.InitializeAsync(path,ct);
            var sessions = new SqliteSessionFactory(path);
            using var files = new SyntheticSecretFiles();
            files.WriteManifest(brands: CheckoutFixture.BrandIds);
            using var secrets = SecretFiles.Load(files.Root,"service-config.json",files.Grant(brands:CheckoutFixture.BrandIds));
            var brand = CheckoutFixture.ResolveContexts()[0];
            Guid user=Guid.NewGuid(), product=Guid.NewGuid(), stock=Guid.NewGuid(), admin=Guid.NewGuid(), audit=Guid.NewGuid();
            const string credential="public synthetic delivery credential";
            byte[] encrypted=new InventoryCipher(secrets).Encrypt(stock,Encoding.UTF8.GetBytes(credential));
            await sessions.RunAsync(true,async (_,tx,token)=>
            {
                using var setup=SqliteWallet.Command(tx,"""
                    INSERT INTO brands(id,code,internal_name,public_name,market,default_language,bot_username,bot_id,
                        bot_token_ciphertext,bot_token_key_id,active)
                    VALUES ($brand,'delivery','Delivery','Delivery','international','en','delivery_bot',1001,x'01','fixture',1);
                    INSERT INTO users(id,brand_id,telegram_id,language) VALUES ($user,$brand,7001,'en');
                    INSERT INTO admins(id,login,authentication_mode,external_subject) VALUES ($admin,'delivery','external','delivery');
                    INSERT INTO audit_events(id,actor_kind,actor_admin_id,brand_id,action,entity_kind,entity_id,reason,correlation_id)
                    VALUES ($audit,'admin',$admin,$brand,'wallet_adjustment','wallet',$user,'fixture',$audit);
                    INSERT INTO wallets(user_id,balance,version) VALUES ($user,'30',1);
                    INSERT INTO wallet_entries(id,user_id,signed_amount,balance_after,posting_version,movement_kind,is_posting,
                        idempotency_key,admin_id,audit_event_id,reason)
                    VALUES ($audit,$user,'30','30',1,'admin_adjustment',1,'funded',$admin,$audit,'fixture');
                    INSERT INTO products(id,fulfillment_kind,availability_policy,title) VALUES ($product,'local','immediate','Fixture');
                    INSERT INTO brand_products(brand_id,product_id,visible,price_usdt) VALUES ($brand,$product,1,'10');
                    INSERT INTO inventory_items(id,product_id,credential_ciphertext,credential_key_id,source_fingerprint,origin_kind)
                    VALUES ($stock,$product,$cipher,'current',$fingerprint,'local_import');
                    """,("$brand",brand.BrandId),("$user",user),("$admin",admin),("$audit",audit),("$product",product),
                    ("$stock",stock),("$cipher",encrypted),("$fingerprint",SHA256.HashData(stock.ToByteArray())));
                await setup.ExecuteNonQueryAsync(token);
                return 0;
            },ct);
            Guid order=Guid.NewGuid();
            var checkout=new SqliteCheckoutStore(sessions,secrets);
            var result=await checkout.PurchaseLocalAsync(brand,0,7001,new(order,product,1,10m),ct);
            Assert.Equal(CheckoutOutcome.Purchased,result.Outcome);
            Guid statusAnchor=Guid.NewGuid();
            await sessions.RunAsync(true,async(_,tx,token)=>
            {
                string now=SqliteSchema.UtcText(DateTime.UtcNow);
                using var anchor=SqliteWallet.Command(tx,"""
                    INSERT INTO telegram_deliveries(id,brand_id,recipient_kind,user_id,purpose,action,dedupe_key,status,
                        telegram_message_id,sent_at,order_id)
                    VALUES ($id,$brand,'customer_dm',$member,'order_status','send','public-anchor','succeeded',789123,$now,$order)
                    """,("$id",statusAnchor),("$brand",brand.BrandId),("$member",user),("$now",now),("$order",order));
                await anchor.ExecuteNonQueryAsync(token);
                return 0;
            },ct);
            var manifestLease=await sessions.RunAsync(true,(_,tx,token)=>
                OutboxStore.ClaimSqliteAsync(tx,brand.BrandId,JobKind.PrepareManifest,Guid.NewGuid(),TimeSpan.FromMinutes(1),token),ct);
            Assert.NotNull(manifestLease);
            var delivery=new SqliteDeliveryStore(new SqliteSessionFactory(path),secrets);
            Guid deliveryId=Assert.IsType<Guid>(await delivery.PrepareAsync(brand,manifestLease,ct));
            byte[] document=await delivery.ReadManifestAsync(brand,7001,order,ct);
            Assert.NotEmpty(document);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                delivery.ReadManifestAsync(CheckoutFixture.ResolveContexts()[1],7001,order,ct));
            var sendLease=await sessions.RunAsync(true,(_,tx,token)=>
                OutboxStore.ClaimSqliteAsync(tx,brand.BrandId,JobKind.SendTelegram,Guid.NewGuid(),TimeSpan.FromMinutes(1),token),ct);
            Assert.NotNull(sendLease);
            using(var dispatch=await delivery.BeginAsync(brand,sendLease,ct))
            {
                Assert.NotNull(dispatch);
                Assert.Equal(deliveryId,dispatch.Id);
                Assert.Equal(789123,dispatch.TargetMessageId);
                Assert.False(dispatch.IsFile);
                Assert.Contains(credential,Encoding.UTF8.GetString(dispatch.Content.Span));
            }
            await delivery.RecordOutcomeAsync(brand,sendLease,new(DeliveryOutcomeKind.Succeeded,789123),ct);
            await delivery.RecordOutcomeAsync(brand,sendLease,new(DeliveryOutcomeKind.Succeeded,789123),ct);
            await new SqliteSessionFactory(path).RunAsync(false,async(_,tx,token)=>
            {
                using var read=SqliteWallet.Command(tx,"""
                    SELECT o.fulfillment_status,f.status,d.status,j.status
                    FROM orders o JOIN fulfillments f ON f.order_id=o.id
                    JOIN telegram_deliveries d ON d.id=$delivery
                    JOIN jobs j ON j.telegram_delivery_id=d.id WHERE o.id=$order
                    """,("$order",order),("$delivery",deliveryId));
                await using var row=await read.ExecuteReaderAsync(token);
                Assert.True(await row.ReadAsync(token));
                Assert.Equal("completed",row.GetString(0));
                Assert.Equal("released",row.GetString(1));
                Assert.Equal("succeeded",row.GetString(2));
                Assert.Equal("succeeded",row.GetString(3));
                return true;
            },ct);
        }
        finally { directory.Delete(recursive:true); }
    }
}
