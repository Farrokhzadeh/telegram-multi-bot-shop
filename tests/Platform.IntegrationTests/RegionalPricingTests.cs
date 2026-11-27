using System.Text;
using Platform.Core;
using Platform.Persistence;
using Platform.Security;
using Platform.Telegram;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class RegionalPricingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static WalletCheckoutService Service(CheckoutFixture f) => new(f.Sessions, f.Store,
        new CatalogStore(f.Sessions, [], (_, _) => throw new InvalidOperationException("Unexpected source.")));
    private static Task<ChannelMembership> Member(long chat, long actor, CancellationToken ct) => Task.FromResult(ChannelMembership.Member);

    [Theory]
    [InlineData(10, CheckoutPayment.Wallet)]
    [InlineData(50, CheckoutPayment.Wallet)]
    [InlineData(100, CheckoutPayment.Wallet)]
    [InlineData(10, CheckoutPayment.Crypto)]
    [InlineData(50, CheckoutPayment.Crypto)]
    [InlineData(100, CheckoutPayment.Crypto)]
    [InlineData(10, CheckoutPayment.Bank)]
    [InlineData(50, CheckoutPayment.Bank)]
    [InlineData(100, CheckoutPayment.Bank)]
    public async Task AllDiscountPercentagesRejectElevenBeforeAnyEffectsThenAcceptExactlyTen(int percent, CheckoutPayment method)
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: percent == 100 ? 0 : 100, stock: 11, ct: Ct);
        await Regional(f);
        var admin = await f.AdminAsync(Ct);
        await using (var issue = await f.Sessions.BeginAsync(Ct))
        {
            await new PricingStore(f.Sessions).IssueAsync(issue, f.Context(0), admin,
                new(Guid.NewGuid(), "PUBLIC-CAPPED", CouponKind.Promotion, percent, f.Product, f.Users[0], MaximumUses: 10), Guid.NewGuid(), Ct);
            await issue.CommitAsync(Ct);
        }
        var service = Service(f);
        var bad = new CheckoutRequest(OrderIds.New(), f.Product, 11, method);
        Assert.Equal(10, (await Assert.ThrowsAsync<DiscountQuantityException>(() => service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), bad, Member, Ct))).Maximum);
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.payments", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", Ct));
        var valid = new CheckoutRequest(OrderIds.New(), f.Product, 10, method);
        bool unpaid = method != CheckoutPayment.Wallet && percent != 100;
        Assert.Equal(unpaid ? CheckoutOutcome.AwaitingPayment : CheckoutOutcome.Purchased,
            (await service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), valid, Member, Ct)).Outcome);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE units_covered=10 AND status='" + (unpaid ? "held" : "redeemed") + "'", Ct));
        Assert.Equal(unpaid ? 0L : 10L, await f.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='allocated'", Ct));
        Assert.Equal(percent == 100 ? 1L : 0L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='not_required' AND payment_method='no_payment_required' AND payment_due_at IS NULL", Ct));
        Assert.Equal(unpaid ? 1L : 0L, await f.CountAsync("SELECT count(*) FROM shop.payments", Ct));
    }

    [Theory]
    [InlineData(CheckoutPayment.Wallet)]
    [InlineData(CheckoutPayment.Crypto)]
    public async Task FixedTomanPricePreservesIndependentWalletAndCryptoAmounts(CheckoutPayment method)
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 100, stock: 2, ct: Ct);
        await Regional(f);
        var request = new CheckoutRequest(OrderIds.New(), f.Product, 2, method);
        var result = await Service(f).PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), request, Member, Ct);
        Assert.Equal(method == CheckoutPayment.Wallet ? CheckoutOutcome.Purchased : CheckoutOutcome.AwaitingPayment, result.Outcome);
        await using var read = await f.Sessions.BeginAsync(Ct);
        var order = await read.QuerySingleAsync(new DbStatement("SELECT currency,payment_method,unit_price_usdt::text,unit_price_toman::text,auto_price_toman_snapshot,quantity FROM shop.orders WHERE id=$1", DbValue.Uuid(request.OrderId)), row => (Currency: row.GetString(0), Method: row.GetString(1), Usdt: ExactDecimal.Parse(row.GetString(2)), Toman: ExactDecimal.Parse(row.GetString(3)), Auto: row.GetBoolean(4), Quantity: row.GetInt32(5)), Ct);
        Assert.Equal(("USDT", method == CheckoutPayment.Wallet ? "wallet" : "crypto", 10m, 1000000m, false, 2), order);
        Assert.Equal(20m, order.Usdt * order.Quantity);
        Assert.Equal(2000000m, order.Toman * order.Quantity);
        Assert.Equal(method == CheckoutPayment.Wallet ? 1L : 0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit' AND signed_amount=-20", Ct));
        Assert.Equal(method == CheckoutPayment.Crypto ? 1L : 0L, await f.CountAsync("SELECT count(*) FROM shop.payments WHERE quote_currency='USD' AND amount_due=20 AND payment_method='crypto'", Ct));
    }

    [Fact]
    public async Task OwnerOvercommitAndExpiryEditsCannotInvalidateAnAlreadyProtectedWholeClaim()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, stock: 3, ct: Ct);
        var admin = await f.AdminAsync(Ct);
        Guid coupon = Guid.NewGuid();
        await using (var issue = await f.Sessions.BeginAsync(Ct))
        {
            await new PricingStore(f.Sessions).IssueAsync(issue, f.Context(0), admin,
                new(coupon, "PUBLIC-PROTECTED", CouponKind.Goodwill, 100, f.Product, f.Users[0], MaximumUses: 3), Guid.NewGuid(), Ct);
            await issue.CommitAsync(Ct);
        }
        var request = new CheckoutRequest(OrderIds.New(), f.Product, 3);
        await Service(f).PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), request, Member, Ct);
        await using (var edit = await f.Sessions.BeginAsync(Ct))
        {
            Guid audit = Guid.NewGuid();
            await edit.ExecuteAsync(new DbStatement("UPDATE shop.discounts SET percent_off=10,max_total_uses=0,usage_adjustment_units=20,ends_at=clock_timestamp()-INTERVAL '1 minute',active=false,revision=revision+1 WHERE id=$1", DbValue.Uuid(coupon)), Ct);
            await edit.ExecuteAsync(new DbStatement("INSERT INTO shop.audit_events(id,actor_kind,actor_admin_id,brand_id,action,entity_kind,entity_id,reason,new_revision,correlation_id) VALUES ($1,'admin',$2,$3,'coupon_edit','discount',$4,'public audited owner override',1,gen_random_uuid())",
                DbValue.Uuid(audit), DbValue.Uuid(admin.AdminId), DbValue.Uuid(f.Brands[0]), DbValue.Uuid(coupon)), Ct);
            await edit.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.discount_revisions(id,discount_id,revision_no,audit_event_id,percent_off,max_total_uses,usage_adjustment_units,
                    redeemed_count_at_revision,held_units_at_revision,ends_at,active,product_id,eligible_user_id)
                SELECT gen_random_uuid(),id,revision,$1,percent_off,max_total_uses,usage_adjustment_units,redeemed_count,0,ends_at,active,product_id,eligible_user_id FROM shop.discounts WHERE id=$2
                """, DbValue.Uuid(audit), DbValue.Uuid(coupon)), Ct);
            await edit.CommitAsync(Ct);
        }
        JobLease prepare;
        await using (var claim = await f.Sessions.BeginAsync(Ct))
        {
            prepare = (await OutboxStore.ClaimAsync(claim, JobKind.PrepareManifest, Guid.NewGuid(), TimeSpan.FromSeconds(30), Ct))!;
            await claim.CommitAsync(Ct);
        }
        await new DeliveryStore(f.Sessions).PrepareAsync(f.Context(0), prepare, Ct);
        Assert.Equal(3L, await f.CountAsync("SELECT redeemed_count FROM shop.discounts", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='redeemed' AND units_covered=3", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE discount_pct_snapshot=100 AND release_authorized_at IS NOT NULL", Ct));
    }

    private static async Task Regional(CheckoutFixture f)
    {
        byte[] number = Encoding.UTF8.GetBytes("public synthetic number");
        byte[] holder = Encoding.UTF8.GetBytes("public synthetic holder");
        await using var edit = await f.Sessions.BeginAsync(Ct);
        await edit.ExecuteAsync(new DbStatement("""
            UPDATE shop.brands SET market='iran',allow_bank=true,allow_crypto=true,policy_version=policy_version+1,bank_number=$1,bank_holder=$2,bank_card_fingerprint=decode(repeat('77',32),'hex'),bank_changed_at=clock_timestamp() WHERE id=$3
            """, DbValue.Binary(number), DbValue.Binary(holder), DbValue.Uuid(f.Brands[0])), Ct);
        await edit.ExecuteAsync(new DbStatement("UPDATE shop.brand_products SET allow_bank=true,allow_crypto=true,auto_price_toman=false,price_toman=1000000,revision=revision+1 WHERE brand_id=$1", DbValue.Uuid(f.Brands[0])), Ct);
        await edit.CommitAsync(Ct);
    }
}
