using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class SqliteAdminCatalogWriteTests
{
    [Fact]
    public async Task SyntheticAdminPolicyProductAndListingWritesAreRevisionedAuditedAndReplayedOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("akanti-sqlite-admin-catalog-writes-");
        try
        {
            string path = Path.Combine(dir.FullName, "shop.db");
            await SqliteSchema.InitializeAsync(path, ct);
            var sessions = new SqliteSessionFactory(path);
            var brand = CheckoutFixture.ResolveContexts()[0];
            var other = CheckoutFixture.ResolveContexts()[1];
            using var adminToken = AdminToken.Create();
            Guid admin = Guid.NewGuid(), session = Guid.NewGuid(), product = Guid.NewGuid();
            await sessions.RunAsync(true, async (_, tx, token) =>
            {
                using var setup = SqliteWallet.Command(tx, """
                    INSERT INTO brands(id,code,internal_name,public_name,market,default_language,bot_username,
                        bot_id,bot_token_ciphertext,bot_token_key_id,active)
                    VALUES ($brand,'stage','Stage','Stage','international','en','stage_bot',$bot,x'01','fixture',1);
                    INSERT INTO admins(id,login,authentication_mode,external_subject)
                    VALUES ($admin,'synthetic-catalog-admin','external','fixture');
                    INSERT INTO admin_sessions(id,admin_id,token_hash,auth_version_at_issue,expires_at)
                    VALUES ($session,$admin,$hash,0,$expires);
                    """, ("$brand", brand.BrandId), ("$bot", brand.BotId),
                    ("$admin", admin), ("$session", session), ("$hash", adminToken.Hash()),
                    ("$expires", SqliteSchema.UtcText(DateTime.UtcNow.AddHours(1))));
                await setup.ExecuteNonQueryAsync(token);
                return 0;
            }, ct);
            var actor = await SqliteAdminSessionStore.AuthenticateAsync(sessions, adminToken, ct);
            var writer = new SqliteAdminCatalogWriteStore(sessions);
            Guid policyId = Guid.NewGuid(), productId = Guid.NewGuid(), listingId = Guid.NewGuid();
            var policy = new BrandPolicyEdit("Stage renamed", "en", true, true, true, false,
                false, false, false, null, false, null);
            Assert.Equal(1, await writer.PolicyAsync(actor, brand, policy, 0, policyId, ct));
            Assert.Equal(1, await writer.PolicyAsync(actor, brand, policy, 0, policyId, ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                writer.PolicyAsync(actor, brand, policy with { PublicName = "Changed" }, 0, policyId, ct));
            var productEdit = new MasterProductEdit("Synthetic catalog product", null,
                null, null, "local", "immediate", true, true);
            Assert.Equal(1, await writer.ProductAsync(actor, product, productEdit, 0, productId, ct));
            Assert.Equal(1, await writer.ProductAsync(actor, product, productEdit, 0, productId, ct));
            var listing = new ListingEdit(true, "inherit", null, "inherit", null, "inherit", null,
                "inherit", null, "10", null, false, true, true, false, 1, 5);
            Assert.Equal(1, await writer.ListingAsync(actor, brand, product, listing, 0, listingId, ct));
            Assert.Equal(1, await new SqliteAdminCatalogWriteStore(new SqliteSessionFactory(path))
                .ListingAsync(actor, brand, product, listing, 0, listingId, ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                writer.ProductAsync(actor, product, productEdit, 0, Guid.NewGuid(), ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                writer.ListingAsync(actor, other, product, listing, 0, Guid.NewGuid(), ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                writer.ListingAsync(actor, brand, product, listing, 0, Guid.NewGuid(), ct));
            await sessions.RunAsync(false, async (_, tx, token) =>
            {
                using var verify = SqliteWallet.Command(tx, """
                    SELECT b.policy_version,p.revision,l.revision,l.price_usdt,
                        (SELECT count(*) FROM audit_events WHERE actor_admin_id=$admin),
                        (SELECT count(*) FROM jobs WHERE job_kind='evaluate_restock' AND product_id=$product),
                        (SELECT count(*) FROM wallet_entries)
                    FROM brands b JOIN brand_products l ON l.brand_id=b.id
                        JOIN products p ON p.id=l.product_id WHERE b.id=$brand AND p.id=$product
                    """, ("$admin", admin), ("$brand", brand.BrandId), ("$product", product));
                await using var row = await verify.ExecuteReaderAsync(token);
                Assert.True(await row.ReadAsync(token));
                Assert.Equal(1, row.GetInt64(0));
                Assert.Equal(1, row.GetInt64(1));
                Assert.Equal(1, row.GetInt64(2));
                Assert.Equal("10", row.GetString(3));
                Assert.Equal(3, row.GetInt64(4));
                Assert.Equal(1, row.GetInt64(5));
                Assert.Equal(0, row.GetInt64(6));
                return 0;
            }, ct);
            await sessions.RunAsync(true, async (_, tx, token) =>
            {
                using var revoke = SqliteWallet.Command(tx,
                    "UPDATE admin_sessions SET revoked_at=$now WHERE id=$session",
                    ("$now", SqliteSchema.UtcText(DateTime.UtcNow)), ("$session", session));
                return await revoke.ExecuteNonQueryAsync(token);
            }, ct);
            await Assert.ThrowsAsync<AdminAuthenticationException>(() =>
                writer.ProductAsync(actor, product, productEdit, 1, Guid.NewGuid(), ct));
        }
        finally { dir.Delete(recursive: true); }
    }
}
