using System.Security.Cryptography;
using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class SqliteCouponAdministrationTests
{
    [Fact]
    public async Task CouponEditKeepsOriginalRevisionAndRejectsStaleOrChangedAdministratorCommand()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("akanti-sqlite-coupon-admin-");
        try
        {
            string path = Path.Combine(dir.FullName, "shop.db");
            await SqliteSchema.InitializePlaintextAsync(path, ct);
            var sessions = SqliteSessionFactory.ForPlaintext(path);
            using var adminToken = AdminToken.Create();
            var brand = CheckoutFixture.ResolveContexts()[0];
            var other = CheckoutFixture.ResolveContexts()[1];
            Guid admin = Guid.NewGuid(), session = Guid.NewGuid(), coupon = Guid.NewGuid(),
                revision = Guid.NewGuid(), audit = Guid.NewGuid(), product = Guid.NewGuid();
            await sessions.RunAsync(true, async (_, tx, token) =>
            {
                using var setup = SqliteWallet.Command(tx, """
                    INSERT INTO brands(id,code,internal_name,public_name,market,default_language,bot_username,
                        bot_id,bot_token,active)
                    VALUES ($brand,'stage','Stage','Stage','international','en','stage_bot',$bot,x'01',1);
                    INSERT INTO admins(id,login,authentication_mode,external_subject)
                    VALUES ($admin,'coupon-admin','external','fixture');
                    INSERT INTO admin_sessions(id,admin_id,token_hash,auth_version_at_issue,expires_at)
                    VALUES ($session,$admin,$hash,0,$expires);
                    INSERT INTO products(id,fulfillment_kind,availability_policy,title)
                    VALUES ($product,'local','immediate','Synthetic goods');
                    INSERT INTO audit_events(id,actor_kind,brand_id,action,entity_kind,entity_id,
                        reason,correlation_id)
                    VALUES ($audit,'system',$brand,'coupon_issue','discount',$coupon,'synthetic',$audit);
                    INSERT INTO discounts(id,brand_id,code_digest,code_content,
                        display_prefix,issuance_batch_id,kind,percent_off,product_id,max_total_uses,
                        max_uses_per_user)
                    VALUES ($coupon,$brand,$digest,x'01','SAVE',$coupon,'promotion','20',
                        $product,10,5);
                    INSERT INTO discount_revisions(id,discount_id,revision_no,audit_event_id,percent_off,
                        max_total_uses,max_uses_per_user,usage_adjustment_units,
                        redeemed_count_at_revision,held_units_at_revision,active,product_id)
                    VALUES ($revision,$coupon,0,$audit,'20',10,5,0,0,0,1,$product);
                    """, ("$brand", brand.BrandId), ("$bot", brand.BotId), ("$admin", admin),
                    ("$session", session), ("$hash", adminToken.Hash()),
                    ("$expires", SqliteSchema.UtcText(DateTime.UtcNow.AddHours(1))),
                    ("$product", product), ("$audit", audit), ("$coupon", coupon),
                    ("$revision", revision), ("$digest", SHA256.HashData(coupon.ToByteArray())));
                await setup.ExecuteNonQueryAsync(token);
                return 0;
            }, ct);
            var actor = await SqliteAdminSessionStore.AuthenticateAsync(sessions, adminToken, ct);
            var owner = new SqliteCouponAdministrationStore(sessions);
            var update = new CouponOwnerEdit(30m, 15, 5, 0, null, null, true, product, null);
            Guid command = Guid.NewGuid();
            Assert.Equal(1, await owner.EditAsync(brand, actor, coupon, 0, update,
                AuditReason.OperatorReview, command, ct));
            Assert.Equal(1, await new SqliteCouponAdministrationStore(SqliteSessionFactory.ForPlaintext(path))
                .EditAsync(brand, actor, coupon, 0, update,
                    AuditReason.OperatorReview, command, ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                owner.EditAsync(brand, actor, coupon, 0, update with { Percent = 40m },
                    AuditReason.OperatorReview, command, ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                owner.EditAsync(brand, actor, coupon, 0, update,
                    AuditReason.OperatorReview, Guid.NewGuid(), ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                owner.EditAsync(brand, actor, coupon, 1, update,
                    AuditReason.OperatorReview, Guid.NewGuid(), ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                owner.EditAsync(other, actor, coupon, 1,
                    update with { Percent = 40m }, AuditReason.OperatorReview,
                    Guid.NewGuid(), ct));
            await sessions.RunAsync(false, async (_, tx, token) =>
            {
                using var query = SqliteWallet.Command(tx, """
                    SELECT d.revision,d.percent_off,
                        (SELECT count(*) FROM discount_revisions WHERE discount_id=$coupon),
                        (SELECT percent_off FROM discount_revisions
                            WHERE discount_id=$coupon AND revision_no=0),
                        (SELECT percent_off FROM discount_revisions
                            WHERE discount_id=$coupon AND revision_no=1),
                        (SELECT count(*) FROM audit_events WHERE entity_id=$coupon AND action='coupon_edit')
                    FROM discounts d WHERE d.id=$coupon
                    """, ("$coupon", coupon));
                await using var row = await query.ExecuteReaderAsync(token);
                Assert.True(await row.ReadAsync(token));
                Assert.Equal(1, row.GetInt64(0));
                Assert.Equal("30", row.GetString(1));
                Assert.Equal(2, row.GetInt64(2));
                Assert.Equal("20", row.GetString(3));
                Assert.Equal("30", row.GetString(4));
                Assert.Equal(1, row.GetInt64(5));
                return 0;
            }, ct);
        }
        finally { dir.Delete(recursive: true); }
    }
}
