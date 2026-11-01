using Platform.Core;
using Platform.Persistence;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class PaidSourceAuthorityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PaidLabelWithoutPostedMoneyCannotAutomaticallyIssueAnEntitlement()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        Guid source = Guid.NewGuid();
        await using (var seed = await f.Sessions.BeginAsync(Ct))
        {
            await seed.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.orders(id,user_id,product_id,quantity,order_kind,payment_status,currency,payment_method,unit_price_usdt,
                    auto_price_toman_snapshot,pricing_rounding_step_snapshot,price_calculation_version,brand_public_name_snapshot,product_title_snapshot,
                    payment_terms_snapshot,fulfillment_kind_snapshot,local_first_snapshot,is_preorder,payment_confirmed_at)
                VALUES ($1,$2,$3,1,'prepaid_units','paid','USDT','wallet',10,false,5000,$4,'public brand','public title','public terms','local',true,false,CURRENT_TIMESTAMP)
                """, DbValue.Uuid(source), DbValue.Uuid(f.Users[0]), DbValue.Uuid(f.Product), DbValue.Text(PricingPolicy.Version)), Ct);
            await seed.CommitAsync(Ct);
        }
        await using (var issue = await f.Sessions.BeginAsync(Ct))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new PricingStore(f.Sessions, f.Secrets).IssuePaidSourceAsync(issue, f.Context(0), source, Ct));
            Assert.Equal(DbSessionState.Aborted, issue.State);
        }
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.discounts", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.audit_events WHERE actor_kind='system'", Ct));
    }

    [Fact]
    public async Task SystemAuditCannotAuthorizeMarketingCreation()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        var admin = await f.AdminAsync(Ct);
        await using (var invalid = await f.Sessions.BeginAsync(Ct))
        {
            Guid id = Guid.NewGuid(), audit = Guid.NewGuid();
            await invalid.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.discounts(id,brand_id,code_digest,code_ciphertext,code_key_id,display_prefix,issuance_batch_id,kind,percent_off,created_by_admin_id)
                VALUES ($1,$2,decode(repeat('33',32),'hex'),decode('01','hex'),'current','PUB',$1,'promotion',10,$3)
                """, DbValue.Uuid(id), DbValue.Uuid(f.Brands[0]), DbValue.Uuid(admin.AdminId)), Ct);
            await invalid.ExecuteAsync(new DbStatement("INSERT INTO shop.audit_events(id,actor_kind,brand_id,action,entity_kind,entity_id,reason,new_revision,correlation_id) VALUES ($1,'system',$2,'coupon_issue','discount',$3,'paid_source_entitlement',0,gen_random_uuid())",
                DbValue.Uuid(audit), DbValue.Uuid(f.Brands[0]), DbValue.Uuid(id)), Ct);
            var error = await Assert.ThrowsAsync<DbOperationException>(() => invalid.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.discount_revisions(id,discount_id,revision_no,audit_event_id,percent_off,usage_adjustment_units,redeemed_count_at_revision,held_units_at_revision,active)
                VALUES (gen_random_uuid(),$1,0,$2,10,0,0,0,true)
                """, DbValue.Uuid(id), DbValue.Uuid(audit)), Ct));
            Assert.Equal(DbFailureKind.Constraint, error.Failure.Kind);
        }
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.discounts", Ct));
    }
}
