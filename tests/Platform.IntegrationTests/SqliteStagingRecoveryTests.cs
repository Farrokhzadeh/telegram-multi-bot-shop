using Platform.Persistence;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class SqliteStagingRecoveryTests
{
    [Fact]
    public async Task TelegramStatusSuccessQueuesManifestOnceAndPersistsAcrossRestart()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("akanti-stage-status-");
        try
        {
            string path = Path.Combine(dir.FullName, "shop.db");
            await SqliteSchema.InitializeAsync(path, ct);
            var first = CheckoutFixture.ResolveContexts()[0];
            var second = CheckoutFixture.ResolveContexts()[1];
            Guid customer = Guid.NewGuid(), product = Guid.NewGuid(), order = Guid.NewGuid(), delivery = Guid.NewGuid();
            var sessions = new SqliteSessionFactory(path);
            await sessions.RunAsync(true, async (_, tx, token) =>
            {
                using var command = SqliteWallet.Command(tx, """
                    INSERT INTO brands(id,code,internal_name,public_name,market,default_language,bot_username,bot_id,
                        bot_token_ciphertext,bot_token_key_id,active)
                    VALUES ($brand,'stage','Stage','Stage','international','en','stage_bot',1001,x'01','fixture',1);
                    INSERT INTO users(id,brand_id,telegram_id,language) VALUES ($member,$brand,7001,'en');
                    INSERT INTO products(id,title,fulfillment_kind,availability_policy)
                    VALUES ($product,'Fixture','local','immediate');
                    INSERT INTO orders(id,user_id,product_id,quantity,order_kind,payment_status,fulfillment_status,
                        payment_method,currency,unit_price_usdt,auto_price_toman_snapshot,pricing_rounding_step_snapshot,
                        price_calculation_version,brand_public_name_snapshot,product_title_snapshot,payment_terms_snapshot,
                        fulfillment_kind_snapshot,local_first_snapshot,is_preorder,payment_confirmed_at)
                    VALUES ($order,$member,$product,1,'product_purchase','paid','acquiring','wallet','USDT','10',false,
                        '5000','synthetic','Stage','Fixture','wallet','local',true,false,$now);
                    INSERT INTO telegram_deliveries(id,brand_id,recipient_kind,user_id,purpose,action,dedupe_key,order_id)
                    VALUES ($delivery,$brand,'customer_dm',$member,'order_status','send','fixture-status',$order);
                    """, ("$brand", first.BrandId), ("$member", customer), ("$product", product),
                    ("$order", order), ("$delivery", delivery), ("$now", SqliteSchema.UtcText(DateTime.UtcNow)));
                await command.ExecuteNonQueryAsync(token);
                await OutboxStore.EnqueueSqliteAsync(tx, JobIntent.Telegram(first.BrandId, delivery), delivery,
                    DateTime.UtcNow, token);
                return true;
            }, ct);
            var jobs = new SqliteStagingJobs(new SqliteSessionFactory(path));
            var statuses = new SqliteStatusDeliveryStore(new SqliteSessionFactory(path));
            Assert.Null(await jobs.ClaimAsync(second, JobKind.SendTelegram, Guid.NewGuid(), ct));
            JobLease? lease = await jobs.ClaimAsync(first, JobKind.SendTelegram, Guid.NewGuid(), ct);
            Assert.NotNull(lease);
            Assert.Equal("order_status", await jobs.PurposeAsync(first, lease, ct));
            SqliteStatusDispatch? dispatch = await statuses.BeginAsync(first, lease, ct);
            Assert.NotNull(dispatch);
            Assert.Equal(7001, dispatch.ChatId);
            Assert.Null(dispatch.TargetMessageId);
            await statuses.RecordOutcomeAsync(first, lease, new(DeliveryOutcomeKind.Succeeded, 901), ct);
            await statuses.RecordOutcomeAsync(first, lease, new(DeliveryOutcomeKind.Succeeded, 901), ct);
            var restored = new SqliteStagingJobs(new SqliteSessionFactory(path));
            JobLease? manifest = await restored.ClaimAsync(first, JobKind.PrepareManifest, Guid.NewGuid(), ct);
            Assert.NotNull(manifest);
            Assert.Equal(order, manifest.Intent.OriginId);
            await sessions.RunAsync(false, async (_, tx, token) =>
            {
                using var query = SqliteWallet.Command(tx, """
                    SELECT d.status,d.telegram_message_id,
                        (SELECT count(*) FROM jobs j WHERE j.job_kind='prepare_manifest' AND j.order_id=$order),
                        (SELECT count(*) FROM jobs j WHERE j.job_kind='send_telegram' AND j.status='succeeded')
                    FROM telegram_deliveries d WHERE d.id=$delivery
                    """, ("$order", order), ("$delivery", delivery));
                await using var rows = await query.ExecuteReaderAsync(token);
                Assert.True(await rows.ReadAsync(token));
                Assert.Equal("succeeded", rows.GetString(0));
                Assert.Equal(901L, rows.GetInt64(1));
                Assert.Equal(1L, rows.GetInt64(2));
                Assert.Equal(1L, rows.GetInt64(3));
                return true;
            }, ct);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public async Task StagingUpdateCursorAndCallbackRevisionRemainFencedAfterReopen()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("akanti-stage-cursor-");
        try
        {
            string path = Path.Combine(dir.FullName, "shop.db");
            await SqliteSchema.InitializeAsync(path, ct);
            var first = CheckoutFixture.ResolveContexts()[0];
            var second = CheckoutFixture.ResolveContexts()[1];
            Guid user = Guid.NewGuid(), product = Guid.NewGuid();
            var sessions = new SqliteSessionFactory(path);
            await sessions.RunAsync(true, async (_, tx, token) =>
            {
                using var command = SqliteWallet.Command(tx, """
                    INSERT INTO brands(id,code,internal_name,public_name,market,default_language,bot_username,bot_id,
                        bot_token_ciphertext,bot_token_key_id,active)
                    VALUES ($brand,'stage','Stage','Stage','international','en','stage_bot',1001,x'01','fixture',1);
                    INSERT INTO users(id,brand_id,telegram_id,language) VALUES ($user,$brand,7001,'en');
                    INSERT INTO products(id,title,fulfillment_kind,availability_policy)
                    VALUES ($product,'Fixture','local','immediate');
                    """, ("$brand", first.BrandId), ("$user", user), ("$product", product));
                await command.ExecuteNonQueryAsync(token);
                return true;
            }, ct);
            var ingress = new SqliteStagingIngress(sessions);
            Assert.True(await ingress.AdmitAsync(first, 100, ct));
            Assert.False(await ingress.AdmitAsync(first, 100, ct));
            Assert.False(await ingress.AdmitAsync(first, 99, ct));
            Assert.False(await ingress.AdmitAsync(second, 101, ct));
            var before = await ingress.ReadFlowAsync(first, 7001, ct);
            var after = await ingress.UpdateFlowAsync(first, 7001, "catalog", product, null, 901, before.Revision, ct);
            Assert.Equal(before.Revision + 1, after.Revision);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                ingress.UpdateFlowAsync(first, 7001, "quantity", product, null, 901, before.Revision, ct));
            var reopened = new SqliteStagingIngress(new SqliteSessionFactory(path));
            Assert.Equal(101, await reopened.NextOffsetAsync(first, ct));
            Assert.False(await reopened.AdmitAsync(first, 100, ct));
            var saved = await reopened.ReadFlowAsync(first, 7001, ct);
            Assert.Equal("catalog", saved.State);
            Assert.Equal(product, saved.Product);
            Assert.Equal(901, saved.Anchor);
            Assert.Equal(after.Revision, saved.Revision);
        }
        finally { dir.Delete(recursive: true); }
    }
}
