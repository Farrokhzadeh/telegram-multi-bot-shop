using Platform.Core;
using Platform.Persistence;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class SqlitePaymentEvidenceTests
{
    [Fact]
    public async Task OriginalTopupIntentAndTrustedInvoiceSettleOnceAcrossReopen()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("akanti-sqlite-invoice-");
        try
        {
            string path = Path.Combine(dir.FullName, "shop.db");
            await SqliteSchema.InitializePlaintextAsync(path, ct);
            var brand = CheckoutFixture.ResolveContexts()[0];
            var other = CheckoutFixture.ResolveContexts()[1];
            Guid member = Guid.NewGuid(), payment = Guid.NewGuid();
            var sessions = SqliteSessionFactory.ForPlaintext(path);
            await sessions.RunAsync(true, async (_, tx, token) =>
            {
                using var command = SqliteWallet.Command(tx, """
                    INSERT INTO brands(id,code,internal_name,public_name,market,default_language,bot_username,bot_id,
                        bot_token,active,allow_crypto,allow_wallet_topup,zero_balance_topup)
                    VALUES ($brand,'stage','Stage','Stage','international','en','stage_bot',$bot,x'01',1,1,1,1);
                    INSERT INTO users(id,brand_id,telegram_id,language) VALUES ($member,$brand,7001,'en');
                    """, ("$brand", brand.BrandId), ("$bot", brand.BotId), ("$member", member));
                await command.ExecuteNonQueryAsync(token);
                return 0;
            }, ct);
            var evidence = new SqlitePaymentEvidenceStore(sessions);
            var amount = new Money(7.12m, Currency.Usd);
            Assert.Equal(payment, await evidence.CreateTopupAsync(brand, 0, 7001, payment, amount,
                CryptoAsset.UsdtBsc, ct));
            Assert.Equal(payment, await evidence.CreateTopupAsync(brand, 0, 7001, payment, amount,
                CryptoAsset.UsdtBsc, ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                evidence.CreateTopupAsync(brand, 0, 7001, payment, new Money(7.13m, Currency.Usd),
                    CryptoAsset.UsdtBsc, ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                evidence.CreateTopupAsync(other, 0, 7001, payment, amount, CryptoAsset.UsdtBsc, ct));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                evidence.CreateTopupAsync(brand, 0, 7001, Guid.NewGuid(), new Money(7.123m, Currency.Usd),
                    CryptoAsset.UsdtBsc, ct));

            await evidence.BindInvoiceAsync(brand, 0, 7001, payment, "123456789", 7.12m, ct);
            await evidence.BindInvoiceAsync(brand, 0, 7001, payment, "123456789", 7.12m, ct);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                evidence.BindInvoiceAsync(brand, 0, 7001, payment, "123456780", 7.12m, ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                evidence.BindInvoiceAsync(other, 0, 7001, payment, "123456789", 7.12m, ct));

            await sessions.RunAsync(true, async (_, tx, token) =>
            {
                using var command = SqliteWallet.Command(tx, """
                    UPDATE payments SET status='confirming',provider_status_code='finished',network_amount='7.12',
                        amount_received='7.12',ipn_key_id='fixture',ipn_signature=zeroblob(64),
                        ipn_payload_sha256=zeroblob(32),ipn_verified_at=$now,first_payment_observed_at=$now,
                        confirmed_at=$now,evidence_asset_ref='protected:synthetic',evidence_digest=zeroblob(32)
                    WHERE id=$payment
                    """, ("$payment", payment), ("$now", SqliteSchema.UtcText(DateTime.UtcNow)));
                Assert.Equal(1, await command.ExecuteNonQueryAsync(token));
                return 0;
            }, ct);
            var settlement = new SqliteTopupSettlementStore(SqliteSessionFactory.ForPlaintext(path));
            PaymentSettlement[] results = await Task.WhenAll(
                settlement.SettleAsync(brand, 0, payment, PaymentSettlementFixture.Lookup, ct),
                settlement.SettleAsync(brand, 0, payment, PaymentSettlementFixture.Lookup, ct));
            Assert.Single(results, x => x.Outcome == PaymentSettlementOutcome.Settled);
            Assert.Single(results, x => x.Outcome == PaymentSettlementOutcome.Replayed);
            var replay = await settlement.SettleAsync(brand, 0, payment,
                (_, _) => throw new InvalidOperationException("A settled invoice must not contact the provider."), ct);
            Assert.Equal(PaymentSettlementOutcome.Replayed, replay.Outcome);
            await sessions.RunAsync(false, async (_, tx, token) =>
            {
                using var query = SqliteWallet.Command(tx, """
                    SELECT w.balance,w.version,
                        (SELECT count(*) FROM wallet_entries WHERE user_id=$member AND movement_kind='wallet_topup'),
                        (SELECT count(*) FROM payments WHERE id=$payment AND status='settled')
                    FROM wallets w WHERE w.user_id=$member
                    """, ("$member", member), ("$payment", payment));
                await using var row = await query.ExecuteReaderAsync(token);
                Assert.True(await row.ReadAsync(token));
                Assert.Equal("7.12", row.GetString(0));
                Assert.Equal(1, row.GetInt64(1));
                Assert.Equal(1, row.GetInt64(2));
                Assert.Equal(1, row.GetInt64(3));
                return 0;
            }, ct);
        }
        finally { dir.Delete(recursive: true); }
    }
}
