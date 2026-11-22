using Platform.Core;
using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class SqlitePaymentCaptureTests
{
    [Fact]
    public async Task VerifiedTopupCallbackArchivesEvidenceAndCreditsExactlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("akanti-sqlite-capture-");
        try
        {
            string path = Path.Combine(dir.FullName, "shop.db");
            await SqliteSchema.InitializeAsync(path, ct);
            var brand = CheckoutFixture.ResolveContexts()[0];
            var other = CheckoutFixture.ResolveContexts()[1];
            var sessions = new SqliteSessionFactory(path);
            using var files = new SyntheticSecretFiles();
            files.WriteManifest(brands: CheckoutFixture.BrandIds);
            using var keys = SecretFiles.Load(files.Root, "service-config.json", files.Grant(brands: CheckoutFixture.BrandIds));
            Guid member = Guid.NewGuid(), payment = Guid.NewGuid();
            await sessions.RunAsync(true, async (_, tx, token) =>
            {
                using var sql = SqliteWallet.Command(tx, """
                    INSERT INTO brands(id,code,internal_name,public_name,market,default_language,bot_username,bot_id,
                        bot_token_ciphertext,bot_token_key_id,active,allow_crypto,allow_wallet_topup,zero_balance_topup)
                    VALUES ($brand,'stage','Stage','Stage','international','en','stage_bot',$bot,x'01','fixture',1,1,1,1);
                    INSERT INTO users(id,brand_id,telegram_id,language) VALUES ($member,$brand,7001,'en');
                    """, ("$brand", brand.BrandId), ("$bot", brand.BotId), ("$member", member));
                await sql.ExecuteNonQueryAsync(token);
                return 0;
            }, ct);
            var evidence = new SqlitePaymentEvidenceStore(sessions);
            Assert.Equal(payment, await evidence.CreateTopupAsync(brand, 0, 7001, payment,
                new Money(10.25m, Currency.Usd), CryptoAsset.UsdtTrc20, ct));
            await evidence.BindInvoiceAsync(brand, 0, 7001, payment, "123456789", 10.4m, ct);
            var capture = new SqlitePaymentCaptureStore(sessions, keys);
            using var notice = PaymentNoticeFixture.Notice(origin: "topup:" + payment.ToString("D"));
            int archives = 0;
            Task<string> Save(ProtectedPaymentEvidence proof, CancellationToken _)
            {
                archives++;
                Assert.Equal(payment, proof.PaymentId);
                Assert.Equal(brand.BrandId, proof.BrandId);
                Assert.NotEmpty(proof.Ciphertext.ToArray());
                return Task.FromResult("protected:synthetic/" + proof.Digest);
            }
            var accepted = await capture.CaptureAsync(brand, 0, notice, Save, ct);
            Assert.Equal(PaymentCaptureOutcome.Ready, accepted.Outcome);
            Assert.Equal(payment, accepted.PaymentId);
            Assert.Equal(PaymentCaptureOutcome.Replayed, (await capture.CaptureAsync(brand, 0, notice, Save, ct)).Outcome);
            Assert.Equal(1, archives);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                capture.CaptureAsync(other, 0, notice, Save, ct));
            var settle = new SqliteTopupSettlementStore(new SqliteSessionFactory(path));
            Assert.Equal(PaymentSettlementOutcome.Settled,
                (await settle.SettleAsync(brand, 0, payment, PaymentSettlementFixture.Lookup, ct)).Outcome);
            Assert.Equal(PaymentSettlementOutcome.Replayed,
                (await settle.SettleAsync(brand, 0, payment,
                    (_, _) => throw new InvalidOperationException("A replay must not look up a provider."), ct)).Outcome);
            await sessions.RunAsync(false, async (_, tx, token) =>
            {
                using var sql = SqliteWallet.Command(tx, """
                    SELECT (SELECT balance FROM wallets WHERE user_id=$member),
                        (SELECT count(*) FROM wallet_entries WHERE user_id=$member AND is_posting=1),
                        (SELECT count(*) FROM jobs WHERE job_kind='verify_payment' AND payment_id=$payment),
                        (SELECT count(*) FROM payments WHERE id=$payment AND status='settled'
                            AND evidence_asset_ref IS NOT NULL AND pending_evidence_ciphertext IS NULL)
                    """, ("$member", member), ("$payment", payment));
                await using var row = await sql.ExecuteReaderAsync(token);
                Assert.True(await row.ReadAsync(token));
                Assert.Equal("10.25", row.GetString(0));
                Assert.Equal(1, row.GetInt64(1));
                Assert.Equal(1, row.GetInt64(2));
                Assert.Equal(1, row.GetInt64(3));
                return 0;
            }, ct);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public async Task AcceptedOrderEvidenceSurvivesArchiveFailureThenSettlesWithCompensation()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("akanti-sqlite-evidence-recovery-");
        try
        {
            string path = Path.Combine(dir.FullName, "shop.db");
            await SqliteSchema.InitializeAsync(path, ct);
            var brand = CheckoutFixture.ResolveContexts()[0];
            var sessions = new SqliteSessionFactory(path);
            using var files = new SyntheticSecretFiles();
            files.WriteManifest(brands: CheckoutFixture.BrandIds);
            using var keys = SecretFiles.Load(files.Root, "service-config.json", files.Grant(brands: CheckoutFixture.BrandIds));
            Guid member = Guid.NewGuid(), product = Guid.NewGuid(), order = Guid.NewGuid(), payment = Guid.NewGuid();
            DateTime created = DateTime.UtcNow;
            string now = SqliteSchema.UtcText(created);
            string deadline = SqliteSchema.UtcText(created.AddMinutes(30));
            await sessions.RunAsync(true, async (_, tx, token) =>
            {
                using var sql = SqliteWallet.Command(tx, """
                    INSERT INTO brands(id,code,internal_name,public_name,market,default_language,bot_username,bot_id,
                        bot_token_ciphertext,bot_token_key_id,active,allow_crypto)
                    VALUES ($brand,'stage','Stage','Stage','international','en','stage_bot',$bot,x'01','fixture',1,1);
                    INSERT INTO users(id,brand_id,telegram_id,language) VALUES ($member,$brand,7001,'en');
                    INSERT INTO products(id,fulfillment_kind,availability_policy,title)
                    VALUES ($product,'local','immediate','Synthetic item');
                    INSERT INTO orders(id,user_id,product_id,quantity,order_kind,payment_status,currency,payment_method,
                        unit_price_usdt,auto_price_toman_snapshot,pricing_rounding_step_snapshot,price_calculation_version,
                        brand_public_name_snapshot,product_title_snapshot,payment_terms_snapshot,fulfillment_kind_snapshot,
                        local_first_snapshot,is_preorder,payment_due_at,created_at,updated_at)
                    VALUES ($order,$member,$product,1,'product_purchase','awaiting_payment','USDT','crypto',
                        '10.25',false,'5000','synthetic','Stage','Synthetic item','synthetic terms','local',true,false,$deadline,$now,$now);
                    INSERT INTO payments(id,order_id,payment_method,quote_currency,amount_due,provider_code,
                        provider_payment_ref,idempotency_ref,network_asset,expected_network_amount,status)
                    VALUES ($payment,$order,'crypto','USD','10.25','nowpayments','123456789',
                        $origin,'USDTTRC20','10.4','awaiting');
                    """, ("$brand", brand.BrandId), ("$bot", brand.BotId), ("$member", member),
                    ("$product", product), ("$order", order), ("$payment", payment),
                    ("$now", now), ("$deadline", deadline), ("$origin", "order:" + order.ToString("D")));
                await sql.ExecuteNonQueryAsync(token);
                return 0;
            }, ct);
            using var notice = PaymentNoticeFixture.Notice(origin: "order:" + order.ToString("D"));
            var capture = new SqlitePaymentCaptureStore(sessions, keys);
            await Assert.ThrowsAsync<IOException>(() => capture.CaptureAsync(brand, 0, notice,
                (_, _) => throw new IOException("Synthetic protected archive unavailable."), ct));
            await sessions.RunAsync(false, async (_, tx, token) =>
            {
                using var sql = SqliteWallet.Command(tx, """
                    SELECT p.status,p.pending_evidence_ciphertext IS NOT NULL,p.evidence_asset_ref IS NULL,
                        o.payment_status,(SELECT count(*) FROM jobs WHERE job_kind='verify_payment')
                    FROM payments p JOIN orders o ON o.id=p.order_id WHERE p.id=$payment
                    """, ("$payment", payment));
                await using var row = await sql.ExecuteReaderAsync(token);
                Assert.True(await row.ReadAsync(token));
                Assert.Equal("confirming", row.GetString(0));
                Assert.True(row.GetBoolean(1));
                Assert.True(row.GetBoolean(2));
                Assert.Equal("confirmation_pending", row.GetString(3));
                Assert.Equal(1, row.GetInt64(4));
                return 0;
            }, ct);
            var recovered = new SqlitePaymentCaptureStore(new SqliteSessionFactory(path), keys);
            await recovered.ResumeArchiveAsync(brand, 0, payment,
                (proof, _) => Task.FromResult("protected:synthetic/" + proof.Digest), ct);
            Assert.Equal(PaymentCaptureOutcome.Replayed,
                (await recovered.CaptureAsync(brand, 0, notice,
                    (_, _) => throw new IOException("Replayed evidence must not be archived again."), ct)).Outcome);
            var settled = await new SqliteOrderPaymentSettlementStore(new SqliteSessionFactory(path), keys)
                .SettleAsync(brand, 0, payment, PaymentSettlementFixture.Lookup, ct);
            Assert.Equal(PaymentSettlementOutcome.Settled, settled.Outcome);
            Assert.Equal(ExternalSettlementRoute.LocalShortageCredit, settled.Route);
            await sessions.RunAsync(false, async (_, tx, token) =>
            {
                using var sql = SqliteWallet.Command(tx, """
                    SELECT (SELECT balance FROM wallets WHERE user_id=$member),
                        (SELECT count(*) FROM wallet_entries WHERE order_id=$order AND is_posting=1),
                        (SELECT count(*) FROM payments WHERE id=$payment AND status='settled'
                            AND evidence_asset_ref IS NOT NULL AND pending_evidence_ciphertext IS NULL),
                        (SELECT fulfillment_status FROM orders WHERE id=$order)
                    """, ("$member", member), ("$order", order), ("$payment", payment));
                await using var row = await sql.ExecuteReaderAsync(token);
                Assert.True(await row.ReadAsync(token));
                Assert.Equal("10.25", row.GetString(0));
                Assert.Equal(1, row.GetInt64(1));
                Assert.Equal(1, row.GetInt64(2));
                Assert.Equal("compensated", row.GetString(3));
                return 0;
            }, ct);
        }
        finally { dir.Delete(recursive: true); }
    }
}
