using System.Security.Cryptography;
using System.Text;
using Platform.Core;
using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class SqliteSupplierRequestTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QualifiedSupplierResultIsRecordedOnceOrHeldForReviewWithoutResubmission(bool unknown)
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("akanti-sqlite-supplier-");
        try
        {
            string path = Path.Combine(dir.FullName, "shop.db");
            await PlainTargetRuntimeTests.InitializeSqliteAsync(path, ct);
            var brand = CheckoutFixture.ResolveContexts()[0];
            var other = CheckoutFixture.ResolveContexts()[1];
            var sessions = SqliteSessionFactory.ForPlaintext(path);
            Guid member = Guid.NewGuid(), product = Guid.NewGuid(), source = Guid.NewGuid();
            Guid order = Guid.NewGuid(), request = Guid.NewGuid();
            await sessions.RunAsync(true, async (_, tx, token) =>
            {
                using var command = SqliteWallet.Command(tx, """
                    INSERT INTO brands(id,code,internal_name,public_name,market,default_language,bot_username,bot_id,
                        bot_token,active)
                    VALUES ($brand,'stage','Stage','Stage','international','en','stage_bot',$bot,x'01',1);
                    INSERT INTO users(id,brand_id,telegram_id,language) VALUES ($member,$brand,7001,'en');
                    INSERT INTO products(id,title,fulfillment_kind,availability_policy)
                    VALUES ($product,'Fixture','supplier','immediate');
                    INSERT INTO product_supplier_sources(id,product_id,provider_code,external_product_id,enabled,revision)
                    VALUES ($source,$product,'synthetic','sku',1,1);
                    INSERT INTO orders(id,user_id,product_id,quantity,order_kind,payment_status,fulfillment_status,
                        payment_method,currency,unit_price_usdt,auto_price_toman_snapshot,pricing_rounding_step_snapshot,
                        price_calculation_version,brand_public_name_snapshot,product_title_snapshot,payment_terms_snapshot,
                        fulfillment_kind_snapshot,local_first_snapshot,is_preorder,payment_confirmed_at)
                    VALUES ($order,$member,$product,1,'product_purchase','paid','acquiring','wallet','USDT','10',false,
                        '5000','synthetic','Stage','Fixture','wallet','supplier',false,false,$now);
                    INSERT INTO supplier_requests(id,order_id,requested_quantity,product_supplier_source_id,
                        source_revision,provider_code,account_ref,external_product_id,local_intent_key,
                        request_sha256,status,cost_baseline_amount,cost_currency,cost_quote_ref,cost_observed_at)
                    VALUES ($request,$order,1,$source,1,'synthetic','public-test-account','sku',
                        $intent,$digest,'pending','5','USD','public-quote',$now);
                    """, ("$brand", brand.BrandId), ("$bot", brand.BotId), ("$member", member),
                    ("$product", product), ("$source", source), ("$order", order), ("$request", request),
                    ("$now", SqliteSchema.UtcText(DateTime.UtcNow)), ("$intent", "supplier:" + request.ToString("D")),
                    ("$digest", SHA256.HashData(request.ToByteArray())));
                await command.ExecuteNonQueryAsync(token);
                await OutboxStore.EnqueueSqliteAsync(tx,
                    JobIntent.Supplier(JobKind.SubmitSupplier, brand.BrandId, request),
                    request, DateTime.UtcNow, token);
                return 0;
            }, ct);
            var supplier = new SqliteSupplierRequestStore(sessions);
            Assert.Null(await supplier.ClaimAsync(other, JobKind.SubmitSupplier, Guid.NewGuid(), ct));
            JobLease? lease = await supplier.ClaimAsync(brand, JobKind.SubmitSupplier, Guid.NewGuid(), ct);
            Assert.NotNull(lease);
            var binding = await supplier.ReadAsync(brand, lease, ct);
            var quote = new SupplierQuote("synthetic", "public-test-account", "sku", null,
                "USD", 5m, true, true, DateTime.UtcNow, "protected:synthetic-quote");
            SupplierAttempt? attempt = await supplier.BeginAsync(brand, lease, quote, true, true, true, ct);
            Assert.NotNull(attempt);
            Assert.Equal(binding, attempt.Binding);
            if (unknown)
            {
                using var uncertain = new SupplierOutcome(SupplierOutcomeKind.Unknown, null, null, null, [], []);
                await supplier.RecordAsync(brand, lease, attempt, uncertain, null, ct);
                await sessions.RunAsync(false, async (_, tx, token) =>
                {
                    using var query = SqliteWallet.Command(tx, """
                        SELECT r.status,r.review_code,o.fulfillment_status,
                            (SELECT count(*) FROM supplier_request_attempts WHERE supplier_request_id=$request),
                            (SELECT count(*) FROM jobs WHERE supplier_request_id=$request
                                AND job_kind='reconcile_supplier' AND status='pending'),
                            (SELECT count(*) FROM fulfillments),
                            (SELECT count(*) FROM wallet_entries)
                        FROM supplier_requests r JOIN orders o ON o.id=r.order_id WHERE r.id=$request
                        """, ("$request", request));
                    await using var row = await query.ExecuteReaderAsync(token);
                    Assert.True(await row.ReadAsync(token));
                    Assert.Equal("unknown", row.GetString(0));
                    Assert.Equal("provider_unknown", row.GetString(1));
                    Assert.Equal("requires_admin", row.GetString(2));
                    Assert.Equal(1, row.GetInt64(3));
                    Assert.Equal(1, row.GetInt64(4));
                    Assert.Equal(0, row.GetInt64(5));
                    Assert.Equal(0, row.GetInt64(6));
                    return 0;
                }, ct);
            }
            else
            {
                using var item = new SupplierItem("remote-item", SupplierItemDisposition.Customer,
                    "code", Encoding.UTF8.GetBytes("synthetic supplier credential"));
                using var supplied = new SupplierOutcome(SupplierOutcomeKind.Completed, "remote-order", "USD", 5m,
                    [item], Encoding.UTF8.GetBytes("synthetic provider response"));
                await supplier.RecordAsync(brand, lease, attempt, supplied,
                    "protected:synthetic/supplier-evidence", ct);
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                    supplier.RecordAsync(brand, lease, attempt, supplied,
                        "protected:synthetic/supplier-evidence", ct));
                await sessions.RunAsync(false, async (_, tx, token) =>
                {
                    using var query = SqliteWallet.Command(tx, """
                        SELECT r.status,r.review_code,
                            (SELECT count(*) FROM fulfillments WHERE order_id=$order AND status='acquired'),
                            (SELECT count(*) FROM jobs WHERE job_kind='prepare_manifest' AND order_id=$order),
                            (SELECT count(*) FROM wallet_entries)
                        FROM supplier_requests r WHERE r.id=$request
                        """, ("$request", request), ("$order", order));
                    await using var row = await query.ExecuteReaderAsync(token);
                    Assert.True(await row.ReadAsync(token));
                    Assert.Equal("succeeded", row.GetString(0));
                    Assert.True(row.IsDBNull(1));
                    Assert.Equal(1, row.GetInt64(2));
                    Assert.Equal(1, row.GetInt64(3));
                    Assert.Equal(0, row.GetInt64(4));
                    return 0;
                }, ct);
            }
        }
        finally { dir.Delete(recursive: true); }
    }
}
