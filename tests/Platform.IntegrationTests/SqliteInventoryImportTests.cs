using System.Text;
using Platform.Persistence;
using Platform.Security;

namespace Platform.IntegrationTests;

public sealed class SqliteInventoryImportTests
{
    [Fact]
    public async Task AdministratorImportsPlaintextOnceAndRejectsChangedCommandAndDuplicateCredentials()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("akanti-sqlite-import-");
        try
        {
            string path = Path.Combine(directory.FullName, "shop.db");
            await PlainTargetRuntimeTests.InitializeSqliteAsync(path, ct);
            var sessions = SqliteSessionFactory.ForPlaintext(path);
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
            var importer = new SqliteInventoryImportStore(sessions);
            Guid itemId = Guid.NewGuid(), command = Guid.NewGuid();
            const string credential = "synthetic stock import credential";
            var items = new[] { new InventoryImportItem(itemId, credential) };
            await importer.ImportAsync(actor, product, items, command, ct);
            await new SqliteInventoryImportStore(SqliteSessionFactory.ForPlaintext(path))
                .ImportAsync(actor, product, items, command, ct);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                importer.ImportAsync(actor, product,
                    [new InventoryImportItem(itemId, credential + " changed")], command, ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                importer.ImportAsync(actor, product,
                    [new InventoryImportItem(Guid.NewGuid(), credential)], Guid.NewGuid(), ct));
            Guid rejected = Guid.NewGuid();
            await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportAsync(actor, product,
                [new(rejected, "synthetic new content before duplicate"), new(Guid.NewGuid(), credential)], Guid.NewGuid(), ct));
            var inventory = await new SqliteAdminCatalogStore(sessions).InventoryAsync(actor, product, null, 10, ct);
            Assert.Equal(itemId, Assert.Single(inventory.Items).Id);
            await sessions.RunAsync(false, async (_, tx, cancel) =>
            {
                using var verify = SqliteWallet.Command(tx, """
                    SELECT i.credential_content,
                        (SELECT count(*) FROM inventory_items WHERE product_id=$product),
                        (SELECT count(*) FROM audit_events WHERE entity_id=$product AND action='stock_import'),
                        (SELECT count(*) FROM jobs WHERE product_id=$product)
                    FROM inventory_items i WHERE i.id=$item
                    """, ("$product", product), ("$item", itemId));
                await using var row = await verify.ExecuteReaderAsync(cancel);
                Assert.True(await row.ReadAsync(cancel));
                Assert.Equal(credential, Encoding.UTF8.GetString(row.GetFieldValue<byte[]>(0)));
                Assert.Equal(1, row.GetInt64(1));
                Assert.Equal(1, row.GetInt64(2));
                Assert.Equal(2, row.GetInt64(3));
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
