using System.Security.Cryptography;
using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class SqliteAdminCatalogTests
{
    [Fact]
    public async Task AuthenticatedAdminReadsArePagedAndSessionBoundAfterRestart()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("akanti-admin-");
        try
        {
            var path = Path.Combine(directory.FullName, "shop.db");
            await SqliteSchema.InitializePlaintextAsync(path, ct);
            var sessions = SqliteSessionFactory.ForPlaintext(path);
            var brand = CheckoutFixture.ResolveContexts()[0];
            using var token = AdminToken.Create();
            Guid actorId = Guid.NewGuid(), sessionId = Guid.NewGuid();
            Guid[] productIds = [Guid.Parse("00000000-0000-0000-0000-000000000001"), Guid.Parse("00000000-0000-0000-0000-000000000002"), Guid.Parse("7fffffff-ffff-ffff-ffff-ffffffffffff"), Guid.Parse("80000000-0000-0000-0000-000000000000"), Guid.Parse("80000000-0000-0000-0000-000000000001"), Guid.Parse("ffffffff-ffff-ffff-ffff-fffffffffffe"), Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff")];
            await sessions.RunAsync(true, async (_, tx, cancel) =>
            {
                using var setup = SqliteWallet.Command(tx, """
                    INSERT INTO brands(id,code,internal_name,public_name,market,default_language,bot_username,bot_id,
                        bot_token,active,policy_version)
                    VALUES ($brand,'admin','Admin','Admin','international','en','admin_bot',1001,x'01',1,1);
                    INSERT INTO admins(id,login,authentication_mode,external_subject) VALUES ($admin,'admin','external','fixture');
                    INSERT INTO admin_sessions(id,admin_id,token_hash,auth_version_at_issue,expires_at)
                    VALUES ($session,$admin,$hash,0,$expires);
                    """, ("$brand", brand.BrandId), ("$admin", actorId), ("$session", sessionId), ("$hash", token.Hash()),
                    ("$expires", SqliteSchema.UtcText(DateTime.UtcNow.AddHours(1))));
                await setup.ExecuteNonQueryAsync(cancel);
                for (int i = 0; i < productIds.Length; i++)
                {
                    using var insert = SqliteWallet.Command(tx, """
                        INSERT INTO products(id,title,fulfillment_kind,availability_policy)
                        VALUES ($id,$name,'local','immediate')
                        """, ("$id", productIds[i]), ("$name", "Product " + i));
                    await insert.ExecuteNonQueryAsync(cancel);
                }
                return 0;
            }, ct);
            var restarted = SqliteSessionFactory.ForPlaintext(path);
            var actor = await SqliteAdminSessionStore.AuthenticateAsync(restarted, token, ct);
            Assert.Equal(actorId, actor.AdminId);
            var catalog = new SqliteAdminCatalogStore(restarted);
            var brands = await catalog.BrandsAsync(actor, null, 5, ct);
            Assert.Single(brands.Items);
            Assert.Null(brands.Next);
            Assert.Equal("Admin", (await catalog.PolicyAsync(actor, brand, ct)).Value.PublicName);
            var fx = await catalog.FxAsync(actor, brand, ct);
            Assert.Null(fx.TomanPerUsd);
            Assert.Null(fx.Source);
            Assert.Empty((await catalog.InventoryAsync(actor, productIds[0], null, 3, ct)).Items);
            var first = await catalog.ProductsAsync(actor, null, 3, ct);
            Assert.Equal(3, first.Items.Length);
            Assert.Equal(productIds.Take(3), first.Items.Select(item => item.Id));
            var next = await catalog.ProductsAsync(actor, first.Next, 3, ct);
            Assert.Equal(productIds.Skip(3).Take(3), next.Items.Select(item => item.Id));
            Assert.NotNull(next.Next);
            var last = await catalog.ProductsAsync(actor, next.Next, 3, ct);
            Assert.Single(last.Items);
            Assert.Null(last.Next);
            await restarted.RunAsync(true, async (_, tx, cancel) =>
            {
                using var revoke = SqliteWallet.Command(tx, "UPDATE admin_sessions SET revoked_at=$now WHERE id=$id",
                    ("$now", SqliteSchema.UtcText(DateTime.UtcNow)), ("$id", sessionId));
                Assert.Equal(1, await revoke.ExecuteNonQueryAsync(cancel));
                return 0;
            }, ct);
            await Assert.ThrowsAsync<AdminAuthenticationException>(() => SqliteAdminSessionStore.AuthenticateAsync(restarted, token, ct));
            await Assert.ThrowsAsync<AdminAuthenticationException>(() => catalog.BrandsAsync(actor, null, 5, ct));
        }
        finally { directory.Delete(recursive: true); }
    }
}
