using System.Text;
using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class SqliteInventoryImportTests
{
    [Fact]
    public async Task InventoryKeyHolderImportsOnceAndRejectsChangedCommandAndDuplicateCredentials()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("akanti-sqlite-import-");
        try
        {
            string path = Path.Combine(directory.FullName, "shop.db");
            await SqliteSchema.InitializeAsync(path, ct);
            using var files = new SyntheticSecretFiles();
            files.WriteManifest(brands: CheckoutFixture.BrandIds);
            using var secrets = SecretFiles.Load(files.Root, "service-config.json",
                files.Grant(brands: CheckoutFixture.BrandIds));
            var sessions = new SqliteSessionFactory(path);
            Guid admin = Guid.NewGuid(), session = Guid.NewGuid(), product = Guid.NewGuid();
            using var token = AdminToken.Create();
            await sessions.RunAsync(true, async (_, tx, cancel) =>
            {
                using var seed = SqliteWallet.Command(tx, """
                    INSERT INTO admins(id,login,authentication_mode,external_subject)
                    VALUES ($admin,'stock-import-admin','external','fixture');
                    INSERT INTO admin_sessions(id,admin_id,token_hash,auth_version_at_issue,expires_at)
                    VALUES ($session,$admin,$hash,0,$expires);
                    INSERT INTO products(id,title,fulfillment_kind,availability_policy)
                    VALUES ($product,'Synthetic stock','local','immediate');
                    """, ("$admin", admin), ("$session", session), ("$product", product),
                    ("$hash", token.Hash()),
                    ("$expires", SqliteSchema.UtcText(DateTime.UtcNow.AddHours(1))));
                await seed.ExecuteNonQueryAsync(cancel);
                return 0;
            }, ct);
            var actor = await SqliteAdminSessionStore.AuthenticateAsync(sessions, token, ct);
            var importer = new SqliteInventoryImportStore(sessions, secrets);
            Guid itemId = Guid.NewGuid(), command = Guid.NewGuid();
            const string credential = "synthetic stock import credential";
            var items = new[] { new InventoryImportItem(itemId, credential) };
            await importer.ImportAsync(actor, product, items, command, ct);
            await new SqliteInventoryImportStore(new SqliteSessionFactory(path), secrets)
                .ImportAsync(actor, product, items, command, ct);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                importer.ImportAsync(actor, product,
                    [new InventoryImportItem(itemId, credential + " changed")], command, ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                importer.ImportAsync(actor, product,
                    [new InventoryImportItem(Guid.NewGuid(), credential)], Guid.NewGuid(), ct));
            await sessions.RunAsync(false, async (_, tx, cancel) =>
            {
                using var verify = SqliteWallet.Command(tx, """
                    SELECT i.credential_ciphertext,i.credential_key_id,
                        (SELECT count(*) FROM inventory_items WHERE product_id=$product),
                        (SELECT count(*) FROM audit_events WHERE entity_id=$product AND action='stock_import'),
                        (SELECT count(*) FROM jobs WHERE product_id=$product)
                    FROM inventory_items i WHERE i.id=$item
                    """, ("$product", product), ("$item", itemId));
                await using var row = await verify.ExecuteReaderAsync(cancel);
                Assert.True(await row.ReadAsync(cancel));
                byte[] plain = new InventoryCipher(secrets).Decrypt(itemId,
                    row.GetString(1), row.GetFieldValue<byte[]>(0));
                try { Assert.Equal(credential, Encoding.UTF8.GetString(plain)); }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
                Assert.Equal(1, row.GetInt64(2));
                Assert.Equal(1, row.GetInt64(3));
                Assert.Equal(2, row.GetInt64(4));
                return 0;
            }, ct);
            await sessions.RunAsync(true, async (_, tx, cancel) =>
            {
                using var revoke = SqliteWallet.Command(tx,
                    "UPDATE admin_sessions SET revoked_at=$now WHERE id=$session",
                    ("$now", SqliteSchema.UtcText(DateTime.UtcNow)), ("$session", session));
                return await revoke.ExecuteNonQueryAsync(cancel);
            }, ct);
            await Assert.ThrowsAsync<AdminAuthenticationException>(() =>
                importer.ImportAsync(actor, product, items, command, ct));
        }
        finally { directory.Delete(recursive: true); }
    }
}
