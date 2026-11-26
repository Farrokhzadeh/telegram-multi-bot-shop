using System.Security.Cryptography;
using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class SqliteSupplierReviewTests
{
    [Fact]
    public async Task CostApprovalRequiresOriginalHeldObligationAndAnAuthenticatedIdempotentAudit()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("akanti-sqlite-supplier-review-");
        try
        {
            string path = Path.Combine(dir.FullName, "shop.db");
            await PlainTargetRuntimeTests.InitializeSqliteAsync(path, ct);
            var sessions = SqliteSessionFactory.ForPlaintext(path);
            var brand = CheckoutFixture.ResolveContexts()[0];
            using var token = AdminToken.Create();
            Guid member = Guid.NewGuid(), product = Guid.NewGuid(), source = Guid.NewGuid();
            Guid order = Guid.NewGuid(), request = Guid.NewGuid(), admin = Guid.NewGuid(), session = Guid.NewGuid();
            await sessions.RunAsync(true, async (_, tx, cancellation) =>
            {
                using var setup = SqliteWallet.Command(tx, """
                    INSERT INTO brands(id,code,internal_name,public_name,market,default_language,bot_username,
                        bot_id,bot_token,active)
                    VALUES ($brand,'stage','Stage','Stage','international','en','stage_bot',$bot,x'01',1);
                    INSERT INTO users(id,brand_id,telegram_id,language) VALUES ($member,$brand,7001,'en');
                    INSERT INTO products(id,title,fulfillment_kind,availability_policy)
                    VALUES ($product,'Fixture','supplier','immediate');
                    INSERT INTO product_supplier_sources(id,product_id,provider_code,external_product_id,enabled,revision)
                    VALUES ($source,$product,'synthetic','sku',1,1);
                    INSERT INTO orders(id,user_id,product_id,quantity,order_kind,payment_status,fulfillment_status,
                        payment_method,currency,unit_price_usdt,auto_price_toman_snapshot,pricing_rounding_step_snapshot,
                        price_calculation_version,brand_public_name_snapshot,product_title_snapshot,payment_terms_snapshot,
                        fulfillment_kind_snapshot,local_first_snapshot,is_preorder,payment_confirmed_at,review_code)
                    VALUES ($order,$member,$product,1,'product_purchase','paid','requires_admin','wallet','USDT','10',
                        false,'5000','synthetic','Stage','Fixture','wallet','supplier',false,false,$now,'cost_limit');
                    INSERT INTO supplier_requests(id,order_id,requested_quantity,product_supplier_source_id,
                        source_revision,provider_code,account_ref,external_product_id,local_intent_key,
                        request_sha256,status,review_code,cost_baseline_amount,cost_currency,cost_quote_ref,cost_observed_at)
                    VALUES ($request,$order,1,$source,1,'synthetic','public-test-account','sku',
                        $intent,$digest,'blocked','cost_limit','5','USD','public-quote',$now);
                    INSERT INTO admins(id,login,authentication_mode,external_subject)
                    VALUES ($admin,'synthetic-admin','external','synthetic');
                    INSERT INTO admin_sessions(id,admin_id,token_hash,auth_version_at_issue,expires_at)
                    VALUES ($session,$admin,$hash,0,$expires);
                    """, ("$brand", brand.BrandId), ("$bot", brand.BotId), ("$member", member),
                    ("$product", product), ("$source", source), ("$order", order), ("$request", request),
                    ("$now", SqliteSchema.UtcText(DateTime.UtcNow)), ("$intent", "supplier:" + request.ToString("D")),
                    ("$digest", SHA256.HashData(request.ToByteArray())), ("$admin", admin),
                    ("$session", session), ("$hash", token.Hash()),
                    ("$expires", SqliteSchema.UtcText(DateTime.UtcNow.AddHours(1))));
                await setup.ExecuteNonQueryAsync(cancellation);
                return 0;
            }, ct);
            var actor = await SqliteAdminSessionStore.AuthenticateAsync(sessions, token, ct);
            var review = new SqliteSupplierReviewStore(sessions);
            Guid command = Guid.NewGuid();
            await review.ApproveCostAsync(actor, brand, request, 0, 8m, "USD", command, ct);
            await review.ApproveCostAsync(actor, brand, request, 0, 8m, "USD", command, ct);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                review.ApproveCostAsync(actor, brand, request, 0, 9m, "USD", command, ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                review.ApproveCostAsync(actor, brand, request, 0, 9m, "USD", Guid.NewGuid(), ct));
            await sessions.RunAsync(false, async (_, tx, cancellation) =>
            {
                using var query = SqliteWallet.Command(tx, """
                    SELECT r.status,r.review_code,r.approved_cost_ceiling,r.approval_audit_id,r.revision,
                        o.fulfillment_status,o.review_code,
                        (SELECT count(*) FROM jobs WHERE supplier_request_id=$request AND job_kind='submit_supplier'),
                        (SELECT count(*) FROM audit_events WHERE id=$command),
                        (SELECT count(*) FROM wallet_entries)
                    FROM supplier_requests r JOIN orders o ON o.id=r.order_id WHERE r.id=$request
                    """, ("$request", request), ("$command", command));
                await using var row = await query.ExecuteReaderAsync(cancellation);
                Assert.True(await row.ReadAsync(cancellation));
                Assert.Equal("pending", row.GetString(0));
                Assert.True(row.IsDBNull(1));
                Assert.Equal("8", row.GetString(2));
                Assert.Equal(command, row.GetGuid(3));
                Assert.Equal(1, row.GetInt64(4));
                Assert.Equal("acquiring", row.GetString(5));
                Assert.True(row.IsDBNull(6));
                Assert.Equal(1, row.GetInt64(7));
                Assert.Equal(1, row.GetInt64(8));
                Assert.Equal(0, row.GetInt64(9));
                return 0;
            }, ct);
        }
        finally { dir.Delete(recursive: true); }
    }
}
