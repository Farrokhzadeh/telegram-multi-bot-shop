using System.Security.Cryptography;
using System.Text;
using Platform.Core;
using Platform.Fulfillment;
using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class SqliteSupplierWorkerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyntheticAdapterCommitsVerifiedAcquisitionOrPreservesUnknownOutcome(bool uncertain)
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("akanti-sqlite-supplier-worker-");
        try
        {
            string path = Path.Combine(dir.FullName, "shop.db");
            await SqliteSchema.InitializeAsync(path, ct);
            var sessions = new SqliteSessionFactory(path);
            var brand = CheckoutFixture.ResolveContexts()[0];
            using var files = new SyntheticSecretFiles();
            files.WriteManifest(brands: CheckoutFixture.BrandIds);
            using var secrets = SecretFiles.Load(files.Root, "service-config.json",
                files.Grant(brands: CheckoutFixture.BrandIds));
            Guid member = Guid.NewGuid(), product = Guid.NewGuid(), source = Guid.NewGuid(),
                order = Guid.NewGuid(), request = Guid.NewGuid();
            await sessions.RunAsync(true, async (_, tx, token) =>
            {
                using var setup = SqliteWallet.Command(tx, """
                    INSERT INTO brands(id,code,internal_name,public_name,market,default_language,bot_username,
                        bot_id,bot_token_ciphertext,bot_token_key_id,active)
                    VALUES ($brand,'stage','Stage','Stage','international','en','stage_bot',$bot,x'01','fixture',1);
                    INSERT INTO users(id,brand_id,telegram_id,language) VALUES ($member,$brand,7001,'en');
                    INSERT INTO products(id,title,fulfillment_kind,availability_policy)
                    VALUES ($product,'Synthetic supplier goods','supplier','immediate');
                    INSERT INTO product_supplier_sources(id,product_id,provider_code,external_product_id,enabled,revision)
                    VALUES ($source,$product,'synthetic','sku',1,1);
                    INSERT INTO orders(id,user_id,product_id,quantity,order_kind,payment_status,fulfillment_status,
                        payment_method,currency,unit_price_usdt,auto_price_toman_snapshot,pricing_rounding_step_snapshot,
                        price_calculation_version,brand_public_name_snapshot,product_title_snapshot,payment_terms_snapshot,
                        fulfillment_kind_snapshot,local_first_snapshot,is_preorder,payment_confirmed_at)
                    VALUES ($order,$member,$product,1,'product_purchase','paid','acquiring','wallet','USDT','10',
                        false,'5000','synthetic','Stage','Synthetic supplier goods','wallet','supplier',false,false,$now);
                    INSERT INTO supplier_requests(id,order_id,requested_quantity,product_supplier_source_id,
                        source_revision,provider_code,account_ref,external_product_id,local_intent_key,
                        request_sha256,status,cost_baseline_amount,cost_currency,cost_quote_ref,cost_observed_at)
                    VALUES ($request,$order,1,$source,1,'synthetic','public-test-account','sku',
                        $intent,$digest,'pending','5','USD','public-quote',$now);
                    """, ("$brand", brand.BrandId), ("$bot", brand.BotId), ("$member", member),
                    ("$product", product), ("$source", source), ("$order", order), ("$request", request),
                    ("$now", SqliteSchema.UtcText(DateTime.UtcNow)),
                    ("$intent", "supplier:" + request.ToString("D")),
                    ("$digest", SHA256.HashData(request.ToByteArray())));
                await setup.ExecuteNonQueryAsync(token);
                await OutboxStore.EnqueueSqliteAsync(tx,
                    JobIntent.Supplier(JobKind.SubmitSupplier, brand.BrandId, request),
                    request, DateTime.UtcNow, token);
                return 0;
            }, ct);
            var store = new SqliteSupplierRequestStore(sessions, secrets);
            JobLease? lease = await store.ClaimAsync(brand, JobKind.SubmitSupplier, Guid.NewGuid(), ct);
            Assert.NotNull(lease);
            ISupplierAdapter adapter = uncertain ? new SyntheticAdapter(true) :
                new SqliteSyntheticSupplierAdapter();
            var archive = new SyntheticArchive();
            await new SqliteSupplierFulfillmentWorker(store, adapter, archive).RunAsync(brand, lease, ct);
            if (adapter is SyntheticAdapter fake) Assert.Equal(1, fake.Submissions);
            Assert.Equal(uncertain ? 0 : 1, archive.Saves);
            await new SqliteSessionFactory(path).RunAsync(false, async (_, tx, token) =>
            {
                using var inspect = SqliteWallet.Command(tx, """
                    SELECT r.status,r.review_code,
                        (SELECT count(*) FROM supplier_request_attempts WHERE supplier_request_id=$request),
                        (SELECT count(*) FROM fulfillments WHERE order_id=$order AND status='acquired'),
                        (SELECT count(*) FROM jobs WHERE job_kind='prepare_manifest' AND order_id=$order),
                        (SELECT count(*) FROM wallet_entries)
                    FROM supplier_requests r WHERE r.id=$request
                    """, ("$request", request), ("$order", order));
                await using var row = await inspect.ExecuteReaderAsync(token);
                Assert.True(await row.ReadAsync(token));
                Assert.Equal(uncertain ? "unknown" : "succeeded", row.GetString(0));
                if (uncertain) Assert.Equal("provider_unknown", row.GetString(1));
                else Assert.True(row.IsDBNull(1));
                Assert.Equal(1, row.GetInt64(2));
                Assert.Equal(uncertain ? 0 : 1, row.GetInt64(3));
                Assert.Equal(uncertain ? 0 : 1, row.GetInt64(4));
                Assert.Equal(0, row.GetInt64(5));
                return 0;
            }, ct);
        }
        finally { dir.Delete(recursive: true); }
    }

    private sealed class SyntheticAdapter(bool uncertain) : ISupplierAdapter
    {
        public string Provider => "synthetic";
        public string Account => "public-test-account";
        public bool ProvenIntentLookup => true;
        public bool UsesPurchaseKey => true;
        public bool SupportsExport => true;
        public int Submissions { get; private set; }

        public Task<SupplierQuote> InspectAsync(SupplierBinding binding, CancellationToken ct) =>
            Task.FromResult(new SupplierQuote("synthetic", "public-test-account", "sku", null,
                "USD", 5m, true, true, DateTime.UtcNow, "protected:synthetic-quote"));

        public Task<SupplierOutcome> SubmitAsync(SupplierBinding binding, decimal maximumUnitCharge,
            CancellationToken ct) => SubmitAttemptAsync(binding, null, maximumUnitCharge, ct);

        public Task<SupplierOutcome> SubmitAttemptAsync(SupplierBinding binding, string? purchaseKey,
            decimal maximumUnitCharge, CancellationToken ct)
        {
            Submissions++;
            if (uncertain) throw new InvalidOperationException("Synthetic provider response is unknown.");
            var item = new SupplierItem("synthetic-remote-item", SupplierItemDisposition.Customer,
                "code", Encoding.UTF8.GetBytes("synthetic supplier credential"));
            return Task.FromResult(new SupplierOutcome(SupplierOutcomeKind.Completed, "synthetic-order",
                "USD", 5m, [item], Encoding.UTF8.GetBytes("synthetic provider evidence")));
        }

        public Task<SupplierOutcome> LookupAsync(SupplierBinding binding, string? originalPurchaseReference,
            CancellationToken ct) => throw new InvalidOperationException("Unexpected supplier lookup.");
    }

    private sealed class SyntheticArchive : ISupplierEvidenceArchive
    {
        public int Saves { get; private set; }
        public Task<string> StoreAsync(SupplierBinding binding, Guid attempt, ReadOnlyMemory<byte> evidence,
            CancellationToken ct)
        {
            Saves++;
            return Task.FromResult("protected:synthetic/supplier-evidence");
        }
    }
}
