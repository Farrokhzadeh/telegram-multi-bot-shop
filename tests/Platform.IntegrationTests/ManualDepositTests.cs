using System.Text;
using Platform.Payments;
using Platform.Persistence;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class ManualDepositTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiptApprovalReplaysOnceAndRetainsPaidShortageWithAllHeldUses(bool shortage)
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, stock: 1, ct: Ct);
        var fixture = new PaymentSettlementFixture(f);
        await fixture.EnableBankAsync(Ct);
        var admin = await f.AdminAsync(Ct);
        await using (var issue = await f.Sessions.BeginAsync(Ct))
        {
            Guid coupon = Guid.NewGuid();
            await new PricingStore(f.Sessions, f.Secrets).IssueAsync(issue, f.Context(0), admin, new(coupon, "PUBLIC-" + coupon.ToString("N"), CouponKind.Promotion, 50, f.Product, f.Users[0], MaximumUses: 1), Guid.NewGuid(), Ct);
            await issue.CommitAsync(Ct);
        }
        var order = await fixture.BankOrderAsync(Ct);
        Assert.Equal(ManualDepositOutcome.Submitted, await fixture.Manual.SubmitAsync(f.Context(0), CheckoutFixture.Actor(0), order.Payment, "protected:synthetic/receipt", Ct));
        Assert.Equal(ManualDepositOutcome.Replayed, await fixture.Manual.SubmitAsync(f.Context(0), CheckoutFixture.Actor(0), order.Payment, "protected:synthetic/receipt", Ct));
        if (shortage)
        {
            await using var retire = await f.Sessions.BeginAsync(Ct);
            await retire.ExecuteAsync(new DbStatement("UPDATE shop.inventory_items SET state='retired'"), Ct);
            await retire.CommitAsync(Ct);
        }
        Guid command = Guid.NewGuid();
        long revision = await Revision(f, order.Order);
        Assert.Equal(ManualDepositOutcome.ConfirmedPaid, await fixture.Manual.DecideAsync(f.Context(0), admin, order.Payment, true, revision, command, "public verified receipt", Ct));
        for (int i = 0; i < 100; i++) Assert.Equal(ManualDepositOutcome.Replayed, await fixture.Manual.DecideAsync(f.Context(0), admin, order.Payment, true, revision, command, "public verified receipt", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.payments WHERE status='settled' AND amount_due=500000 AND amount_received=500000 AND confirmed_at IS NOT NULL", Ct));
        Assert.Equal(shortage ? 0 : 1, await f.CountAsync("SELECT count(*) FROM shop.fulfillments", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='held' AND units_covered=1", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", Ct));
        if (shortage)
        {
            Guid review = Guid.NewGuid();
            long current = await Revision(f, order.Order);
            await fixture.Manual.ReviewPaidUnfulfilledAsync(f.Context(0), admin, order.Payment, current, review, "public staff reconciliation", Ct);
            await fixture.Manual.ReviewPaidUnfulfilledAsync(f.Context(0), admin, order.Payment, current, review, "public staff reconciliation", Ct);
            Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='paid' AND fulfillment_status='waiting_stock' AND resolution_kind IS NULL", Ct));
            Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='held'", Ct));
        }
    }

    [Fact]
    public async Task DenialIsUnverifiedAndAnIndependentLaterApprovalRequiresCurrentAdminAndRevision()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, ct: Ct);
        var fixture = new PaymentSettlementFixture(f);
        await fixture.EnableBankAsync(Ct);
        var order = await fixture.BankOrderAsync(Ct);
        var admin = await f.AdminAsync(Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manual.DecideAsync(f.Context(0), admin, order.Payment, true, 0, Guid.NewGuid(), "public decision", Ct));
        await fixture.Manual.SubmitAsync(f.Context(0), CheckoutFixture.Actor(0), order.Payment, "protected:synthetic/receipt", Ct);
        long revision = await Revision(f, order.Order);
        Assert.Equal(ManualDepositOutcome.DeniedUnverified, await fixture.Manual.DecideAsync(f.Context(0), admin, order.Payment, false, revision, Guid.NewGuid(), "public unverified receipt", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='under_review'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manual.DecideAsync(f.Context(0), admin, order.Payment, true, revision + 1, Guid.NewGuid(), "public decision", Ct));
        Assert.Equal(ManualDepositOutcome.ConfirmedPaid, await fixture.Manual.DecideAsync(f.Context(0), admin, order.Payment, true, revision, Guid.NewGuid(), "public verified receipt", Ct));
    }

    [Fact]
    public async Task ForeignOrChangedReceiptAndStaleCardWritesRejectWithoutMutation()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, ct: Ct);
        var fixture = new PaymentSettlementFixture(f);
        await fixture.EnableBankAsync(Ct);
        var order = await fixture.BankOrderAsync(Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Manual.SubmitAsync(f.Context(1), CheckoutFixture.Actor(1), order.Payment, "protected:synthetic/receipt", Ct));
        await fixture.Manual.SubmitAsync(f.Context(0), CheckoutFixture.Actor(0), order.Payment, "protected:synthetic/receipt", Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manual.SubmitAsync(f.Context(0), CheckoutFixture.Actor(0), order.Payment, "protected:synthetic/other", Ct));
        using var bank = new BankDestinationService(f.Sessions, f.Secrets, new byte[32]);
        var admin = await f.AdminAsync(Ct);
        long revision;
        await using (var read = await f.Sessions.BeginAsync(Ct)) revision = await read.QuerySingleAsync(new DbStatement("SELECT policy_version FROM shop.brands WHERE id=$1", DbValue.Uuid(f.Brands[0])), r => r.GetInt64(0), Ct);
        Guid command = Guid.NewGuid();
        await bank.SaveAsync(f.Context(0), admin, "5555666677778888", "Public next holder", revision, command, Ct);
        await bank.SaveAsync(f.Context(0), admin, "5555666677778888", "Public next holder", revision, command, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bank.SaveAsync(f.Context(0), admin, "9999000011112222", "Public stale holder", revision, Guid.NewGuid(), Ct));
        await using var readOld = await f.Sessions.BeginAsync(Ct);
        var value = await readOld.QuerySingleAsync(new DbStatement("SELECT bank_destination_ciphertext,bank_destination_key_id FROM shop.payments WHERE id=$1", DbValue.Uuid(order.Payment)), r => (Bytes: r.GetBinary(0), Key: r.GetString(1)), Ct);
        byte[] plain = new Platform.Security.BrandCipher(f.Secrets).Decrypt(f.Context(0), "payments", order.Payment, "bank_destination_ciphertext", value.Key, value.Bytes);
        try { Assert.Contains("1111222233334444", Encoding.UTF8.GetString(plain), StringComparison.Ordinal); Assert.DoesNotContain("5555666677778888", Encoding.UTF8.GetString(plain), StringComparison.Ordinal); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
    }

    [Fact]
    public async Task SharedKeyedCardFingerprintRejectsReuseAcrossIranBrands()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        var fixture = new PaymentSettlementFixture(f);
        await fixture.EnableBankAsync(Ct);
        await using (var edit = await f.Sessions.BeginAsync(Ct))
        { await edit.ExecuteAsync(new DbStatement("UPDATE shop.brands SET market='iran',policy_version=policy_version+1 WHERE id=$1", DbValue.Uuid(f.Brands[1])), Ct); await edit.CommitAsync(Ct); }
        using var bank = new BankDestinationService(f.Sessions, f.Secrets, new byte[32]);
        var admin = await f.AdminAsync(Ct);
        var error = await Assert.ThrowsAsync<DbOperationException>(() => bank.SaveAsync(f.Context(1), admin, "1111222233334444", "Public duplicate", 1, Guid.NewGuid(), Ct));
        Assert.Equal(DbFailureKind.Constraint, error.Failure.Kind);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.brands WHERE allow_bank", Ct));
    }
    [Fact]
    public async Task TerminalBankReceiptRemainsStaffReviewWithoutCreditOrOrderRevival()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, ct: Ct);
        var fixture = new PaymentSettlementFixture(f);
        await fixture.EnableBankAsync(Ct);
        var order = await fixture.BankOrderAsync(Ct);
        await using (var expire = await f.Sessions.BeginAsync(Ct))
        {
            await expire.ExecuteAsync(new DbStatement("UPDATE shop.orders SET payment_status='expired',fulfillment_status='cancelled',resolution_kind='unpaid_expiry',resolution_at=clock_timestamp(),revision=revision+1"), Ct);
            await expire.CommitAsync(Ct);
        }
        Assert.Equal(ManualDepositOutcome.LateReview, await fixture.Manual.SubmitAsync(f.Context(0), CheckoutFixture.Actor(0), order.Payment, "protected:synthetic/late-receipt", Ct));
        var admin = await f.AdminAsync(Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manual.DecideAsync(f.Context(0), admin, order.Payment, true, 1, Guid.NewGuid(), "public late bank review", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.payments WHERE status='review' AND amount_received IS NULL", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='expired' AND resolution_kind='unpaid_expiry'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments", Ct));
    }
    private static async Task<long> Revision(CheckoutFixture f, Guid order)
    {
        await using var read = await f.Sessions.BeginAsync(Ct);
        return await read.QuerySingleAsync(new DbStatement("SELECT revision FROM shop.orders WHERE id=$1", DbValue.Uuid(order)), r => r.GetInt64(0), Ct);
    }
}
