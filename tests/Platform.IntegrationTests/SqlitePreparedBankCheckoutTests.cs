using System.Security.Cryptography;
using System.Text;
using Platform.Core;
using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class SqlitePreparedBankCheckoutTests
{
    [Fact]
    public async Task FrozenBankCheckoutRetainsEncryptedDestinationAndDoesNotDebitWallet()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("akanti-sqlite-bank-");
        try
        {
            string path = Path.Combine(dir.FullName, "shop.db");
            await SqliteSchema.InitializeAsync(path, ct);
            var sessions = new SqliteSessionFactory(path);
            using var files = new SyntheticSecretFiles();
            files.WriteManifest(brands: CheckoutFixture.BrandIds);
            using var secrets = SecretFiles.Load(files.Root, "service-config.json", files.Grant(brands: CheckoutFixture.BrandIds));
            var brand = CheckoutFixture.ResolveContexts()[0];
            Guid member = Guid.NewGuid(), product = Guid.NewGuid(), order = Guid.NewGuid();
            using var adminToken = AdminToken.Create();
            Guid admin = Guid.NewGuid(), adminSession = Guid.NewGuid();
            const string card = "1111222233334444", holder = "Public synthetic account";
            var cipher = new BrandCipher(secrets);
            byte[] encryptedNumber = cipher.Encrypt(brand, "brands", brand.BrandId,
                "bank_number_ciphertext", Encoding.UTF8.GetBytes(card));
            byte[] encryptedHolder = cipher.Encrypt(brand, "brands", brand.BrandId,
                "bank_holder_ciphertext", Encoding.UTF8.GetBytes(holder));
            await sessions.RunAsync(true, async (_, tx, token) =>
            {
                using var setup = SqliteWallet.Command(tx, """
                    INSERT INTO brands(id,code,internal_name,public_name,market,default_language,bot_username,bot_id,
                        bot_token_ciphertext,bot_token_key_id,active,allow_bank,bank_number_ciphertext,
                        bank_holder_ciphertext,bank_key_id,bank_card_fingerprint,bank_changed_at)
                    VALUES ($brand,'stage','Stage','Stage','iran','fa','stage_bot',$bot,x'01','fixture',1,1,
                        $number,$holder,$key,$fingerprint,$now);
                    INSERT INTO users(id,brand_id,telegram_id,language) VALUES ($member,$brand,7001,'fa');
                    INSERT INTO products(id,fulfillment_kind,availability_policy,title)
                    VALUES ($product,'manual','immediate','Synthetic goods');
                    INSERT INTO brand_products(brand_id,product_id,visible,price_usdt,price_toman,allow_bank)
                    VALUES ($brand,$product,1,'10.25','1000000',1);
                    INSERT INTO admins(id,login,authentication_mode,external_subject)
                    VALUES ($admin,'synthetic-admin','external','synthetic');
                    INSERT INTO admin_sessions(id,admin_id,token_hash,auth_version_at_issue,expires_at)
                    VALUES ($session,$admin,$hash,0,$expires);
                    """, ("$brand", brand.BrandId), ("$bot", brand.BotId), ("$number", encryptedNumber),
                    ("$holder", encryptedHolder), ("$key", secrets.CurrentBrandKeyId(brand)),
                    ("$fingerprint", SHA256.HashData(Encoding.UTF8.GetBytes(card))),
                    ("$now", SqliteSchema.UtcText(DateTime.UtcNow)), ("$member", member), ("$product", product),
                    ("$admin", admin), ("$session", adminSession), ("$hash", adminToken.Hash()),
                    ("$expires", SqliteSchema.UtcText(DateTime.UtcNow.AddHours(1))));
                await setup.ExecuteNonQueryAsync(token);
                return 0;
            }, ct);
            var catalog = new SqliteCatalogStore(sessions, secrets);
            PreparedCheckout prepared = await catalog.PrepareAsync(brand, 7001,
                new CatalogQuery(product, 1, CheckoutPayment.Bank), ct);
            var checkout = new SqlitePreparedCheckoutStore(sessions, secrets);
            var request = new CheckoutRequest(order, product, 1, CheckoutPayment.Bank);
            Assert.Equal(CheckoutOutcome.AwaitingPayment,
                (await checkout.PurchaseAsync(brand, 0, 7001, request, prepared, catalog, ct)).Outcome);
            Assert.Equal(CheckoutOutcome.Replayed,
                (await checkout.PurchaseAsync(brand, 0, 7001, request, prepared, catalog, ct)).Outcome);
            var scopedAdmin = await SqliteAdminSessionStore.AuthenticateAsync(sessions, adminToken, ct);
            var orders = new SqliteAdminOrderStore(sessions);
            var page = await orders.SearchAsync(scopedAdmin, brand, member, order, null, 10, ct);
            Assert.Equal(order, Assert.Single(page.Items).Id);
            Assert.Null(page.Next);
            Assert.Empty((await orders.SearchAsync(scopedAdmin, CheckoutFixture.ResolveContexts()[1],
                null, null, null, 10, ct)).Items);
            await sessions.RunAsync(false, async (_, tx, token) =>
            {
                using var command = SqliteWallet.Command(tx, """
                    SELECT p.id,p.quote_currency,p.amount_due,p.bank_destination_ciphertext,
                        p.bank_destination_key_id,o.payment_status,
                        (SELECT count(*) FROM wallet_entries)
                    FROM payments p JOIN orders o ON o.id=p.order_id WHERE o.id=$order
                    """, ("$order", order));
                await using var row = await command.ExecuteReaderAsync(token);
                Assert.True(await row.ReadAsync(token));
                Assert.Equal("TOMAN", row.GetString(1));
                Assert.Equal("1000000", row.GetString(2));
                Assert.Equal("awaiting_payment", row.GetString(5));
                Assert.Equal(0, row.GetInt64(6));
                byte[] plain = cipher.Decrypt(brand, "payments", row.GetGuid(0),
                    "bank_destination_ciphertext", row.GetString(4), row.GetFieldValue<byte[]>(3));
                try
                {
                    using var stream = new MemoryStream(plain);
                    using var reader = new BinaryReader(stream, Encoding.UTF8);
                    Assert.Equal(1, reader.ReadInt32());
                    int length = reader.ReadInt32();
                    Assert.Equal(card, Encoding.UTF8.GetString(reader.ReadBytes(length)));
                    length = reader.ReadInt32();
                    Assert.Equal(holder, Encoding.UTF8.GetString(reader.ReadBytes(length)));
                }
                finally { CryptographicOperations.ZeroMemory(plain); }
                return 0;
            }, ct);
            var receipt = new SqliteManualDepositStore(new SqliteSessionFactory(path));
            Guid payment = await PaymentId();
            Assert.Equal(SqliteReceiptOutcome.Submitted,
                await receipt.SubmitAsync(brand, 0, 7001, payment, "protected:synthetic/bank-receipt", ct));
            Assert.Equal(SqliteReceiptOutcome.Replayed,
                await receipt.SubmitAsync(brand, 0, 7001, payment, "protected:synthetic/bank-receipt", ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                receipt.SubmitAsync(brand, 0, 7001, payment, "protected:synthetic/changed", ct));
            await sessions.RunAsync(false, async (_, tx, token) =>
            {
                using var sql = SqliteWallet.Command(tx, """
                    SELECT p.status,o.payment_status,
                        (SELECT count(*) FROM wallet_entries)
                    FROM payments p JOIN orders o ON o.id=p.order_id WHERE o.id=$order
                    """, ("$order", order));
                await using var row = await sql.ExecuteReaderAsync(token);
                Assert.True(await row.ReadAsync(token));
                Assert.Equal("observed", row.GetString(0));
                Assert.Equal("under_review", row.GetString(1));
                Assert.Equal(0, row.GetInt64(2));
                return 0;
            }, ct);

            var authenticated = await SqliteAdminSessionStore.AuthenticateAsync(sessions, adminToken, ct);
            var review = new SqliteBankReviewStore(sessions, secrets);
            long revision = await sessions.RunAsync(false, async (_, tx, token) =>
            {
                using var sql = SqliteWallet.Command(tx, "SELECT revision FROM orders WHERE id=$order",
                    ("$order", order));
                return (long)(await sql.ExecuteScalarAsync(token))!;
            }, ct);
            Guid decision = Guid.NewGuid();
            Assert.Equal(SqliteBankDecisionOutcome.ConfirmedPaid,
                await review.DecideAsync(brand, authenticated, payment, true, revision,
                    decision, "Synthetic receipt accepted", ct));
            Assert.Equal(SqliteBankDecisionOutcome.Replayed,
                await review.DecideAsync(brand, authenticated, payment, true, revision,
                    decision, "Synthetic receipt accepted", ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                review.DecideAsync(brand, authenticated, payment, false, revision,
                    decision, "Synthetic receipt rejected", ct));
            await sessions.RunAsync(false, async (_, tx, token) =>
            {
                using var sql = SqliteWallet.Command(tx, """
                    SELECT p.status,p.amount_received,p.review_admin_id,o.payment_status,o.fulfillment_status,
                        (SELECT count(*) FROM wallet_entries),
                        (SELECT count(*) FROM audit_events WHERE id=$command),
                        (SELECT count(*) FROM telegram_deliveries WHERE order_id=$order AND purpose='order_status')
                    FROM payments p JOIN orders o ON o.id=p.order_id WHERE o.id=$order
                    """, ("$order", order), ("$command", decision));
                await using var row = await sql.ExecuteReaderAsync(token);
                Assert.True(await row.ReadAsync(token));
                Assert.Equal("settled", row.GetString(0));
                Assert.Equal("1000000", row.GetString(1));
                Assert.Equal(admin, row.GetGuid(2));
                Assert.Equal("paid", row.GetString(3));
                Assert.Equal("requires_admin", row.GetString(4));
                Assert.Equal(0, row.GetInt64(5));
                Assert.Equal(1, row.GetInt64(6));
                Assert.Equal(2, row.GetInt64(7));
                return 0;
            }, ct);

            Task<Guid> PaymentId() => sessions.RunAsync(false, async (_, tx, token) =>
            {
                using var command = SqliteWallet.Command(tx, "SELECT id FROM payments WHERE order_id=$order",
                    ("$order", order));
                return Guid.Parse((string)(await command.ExecuteScalarAsync(token))!);
            }, ct);
        }
        finally { dir.Delete(recursive: true); }
    }
}
