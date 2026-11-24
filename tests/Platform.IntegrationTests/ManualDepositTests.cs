using System.Text;
using Npgsql;
using Platform.Core;
using Platform.Payments;
using Platform.Persistence;
using Platform.Telegram;
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task QueuedReceiptAndReplacementPreserveOriginalEvidenceAndCoupon(bool receiptFirst)
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, stock: 2, ct: Ct);
        var fixture = new PaymentSettlementFixture(f);
        await fixture.EnableBankAsync(Ct);
        await fixture.EnableAsync(Ct);
        await using (var issue = await f.Sessions.BeginAsync(Ct))
        {
            await new PricingStore(f.Sessions, f.Secrets).IssueAsync(issue, f.Context(0), await f.AdminAsync(Ct), new(Guid.NewGuid(), "PUBLIC-RACE-COUPON", CouponKind.Promotion, 50, f.Product, f.Users[0], MaximumUses: 2), Guid.NewGuid(), Ct);
            await issue.CommitAsync(Ct);
        }
        var original = await fixture.BankOrderAsync(Ct);
        var replacement = new CheckoutRequest(OrderIds.New(), f.Product, 1, CheckoutPayment.Crypto);
        var query = new CatalogQuery(f.Product, 1, CheckoutPayment.Crypto);
        var catalog = fixture.Catalog;
        var prepared = await catalog.PrepareAsync(f.Context(0), CheckoutFixture.Actor(0), query, Ct);
        BrandPolicySnapshot policy;
        await using (var policyRead = await f.Sessions.BeginAsync(Ct)) policy = await BrandPolicyStore.ReadAsync(policyRead, f.Context(0), CheckoutFixture.Actor(0), Ct);
        ChannelAccess access = (await BrandPolicy.CheckChannelsAsync(policy, CheckoutFixture.Actor(0), (_, _, _) => Task.FromResult(ChannelMembership.Member), Ct))!;
        var service = new WalletCheckoutService(f.Sessions, f.Store, catalog);
        await using var barrier = await f.Sessions.BeginAsync(Ct);
        await barrier.ExecuteAsync(new DbStatement("SELECT id FROM shop.users WHERE id=$1 FOR UPDATE", DbValue.Uuid(f.Users[0])), Ct);
        Task<ManualDepositOutcome>? submit = null;
        Task<CheckoutResult>? checkout = null;
        void StartReceipt() => submit = fixture.Manual.SubmitAsync(f.Context(0), CheckoutFixture.Actor(0), original.Payment, "protected:synthetic/race-receipt", Ct);
        void StartCheckout() => checkout = service.AcceptAsync(f.Context(0), CheckoutFixture.Actor(0), replacement, prepared, access, Ct);
        if (receiptFirst) StartReceipt(); else StartCheckout();
        await WaitForUserLocksAsync(f, 1);
        if (receiptFirst) StartCheckout(); else StartReceipt();
        await WaitForUserLocksAsync(f, 2);
        await barrier.CommitAsync(Ct);
        await Task.WhenAll(submit!, checkout!);
        var receiptResult = await submit!;
        var checkoutResult = await checkout!;
        Assert.Equal(CheckoutOutcome.AwaitingPayment, checkoutResult.Outcome);
        Assert.Equal(receiptFirst ? ManualDepositOutcome.Submitted : ManualDepositOutcome.LateReview, receiptResult);
        await using var read = await f.Sessions.BeginAsync(Ct);
        var old = await read.QuerySingleAsync(new DbStatement("SELECT o.payment_status,p.status,p.receipt_asset_ref,p.order_id,c.status FROM shop.payments p JOIN shop.orders o ON o.id=p.order_id JOIN shop.discount_uses c ON c.order_id=o.id WHERE p.id=$1", DbValue.Uuid(original.Payment)), row => (OrderStatus: row.GetString(0), PaymentStatus: row.GetString(1), Receipt: row.GetString(2), Order: row.GetGuid(3), Claim: row.GetString(4)), Ct);
        Assert.Equal(original.Order, old.Order);
        Assert.Equal("protected:synthetic/race-receipt", old.Receipt);
        Assert.Equal(receiptFirst ? ("under_review", "observed", "held") : ("cancelled", "review", "released"), (old.OrderStatus, old.PaymentStatus, old.Claim));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='awaiting_payment'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE order_id='" + replacement.OrderId.ToString("D") + "' AND status='held'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments", Ct));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReceiptAndExpiryPreserveOriginalEvidenceInBothOrderings(bool receiptFirst)
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, ct: Ct);
        var fixture = new PaymentSettlementFixture(f);
        await fixture.EnableBankAsync(Ct);
        await using (var issue = await f.Sessions.BeginAsync(Ct))
        {
            await new PricingStore(f.Sessions, f.Secrets).IssueAsync(issue, f.Context(0), await f.AdminAsync(Ct), new(Guid.NewGuid(), "PUBLIC-RECEIPT-EXPIRY", CouponKind.Promotion, 50, f.Product, f.Users[0], MaximumUses: 1), Guid.NewGuid(), Ct);
            await issue.CommitAsync(Ct);
        }
        var order = await fixture.BankOrderAsync(Ct);
        foreach (string sql in new[] {
            "CREATE SCHEMA receipt_probe",
            "CREATE TABLE receipt_probe.instant (utc timestamptz NOT NULL)",
            "INSERT INTO receipt_probe.instant SELECT payment_due_at FROM shop.orders",
            "CREATE FUNCTION receipt_probe.clock_timestamp() RETURNS timestamptz LANGUAGE sql VOLATILE AS 'SELECT utc FROM receipt_probe.instant'" })
        {
            await using var setup = f.Database.Data.CreateCommand(sql);
            await setup.ExecuteNonQueryAsync(Ct);
        }
        var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SHOP_TEST_POSTGRES_ADMIN"))
        {
            Database = new NpgsqlConnectionStringBuilder(f.Database.Data.ConnectionString).Database,
            Options = "-c search_path=receipt_probe,pg_catalog,public"
        };
        await using var controlled = new NpgsqlSlimDataSourceBuilder(options.ConnectionString).EnableArrays().Build();
        var sessions = new DbSessionFactory(controlled);
        var manual = new ManualDepositService(sessions, new PaymentSettlementStore(sessions, f.Secrets, fixture.Catalog));
        Task<ManualDepositOutcome> Submit() => manual.SubmitAsync(f.Context(0), CheckoutFixture.Actor(0), order.Payment, "protected:synthetic/expiry-receipt", Ct);
        if (receiptFirst) Assert.Equal(ManualDepositOutcome.Submitted, await Submit());
        await using (var advance = f.Database.Data.CreateCommand("UPDATE receipt_probe.instant SET utc=utc+INTERVAL '1 microsecond'"))
            await advance.ExecuteNonQueryAsync(Ct);
        JobLease? lease;
        await using (var claim = await sessions.BeginAsync(Ct))
        {
            lease = await OutboxStore.ClaimAsync(claim, JobKind.ExpireOrderPayment, Guid.NewGuid(), TimeSpan.FromMinutes(1), Ct);
            await claim.CommitAsync(Ct);
        }
        Assert.NotNull(lease);
        PaymentExpiryOutcome result = await new PaymentExpiryService(sessions, f.Secrets).RunAsync(f.Context(0), lease, Ct);
        Assert.Equal(receiptFirst ? PaymentExpiryOutcome.Protected : PaymentExpiryOutcome.Expired, result);
        if (!receiptFirst) Assert.Equal(ManualDepositOutcome.LateReview, await Submit());
        await using var read = await f.Sessions.BeginAsync(Ct);
        var state = await read.QuerySingleAsync(new DbStatement("""
            SELECT o.payment_status,p.status,p.receipt_asset_ref,c.status,p.first_payment_observed_at=o.payment_due_at
            FROM shop.orders o JOIN shop.payments p ON p.order_id=o.id JOIN shop.discount_uses c ON c.order_id=o.id
            WHERE o.id=$1 AND p.id=$2
            """, DbValue.Uuid(order.Order), DbValue.Uuid(order.Payment)),
            row => (row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3), row.GetBoolean(4)), Ct);
        Assert.Equal(receiptFirst ? ("under_review", "observed", "protected:synthetic/expiry-receipt", "held", true) :
            ("expired", "review", "protected:synthetic/expiry-receipt", "released", false), state);
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", Ct));
    }

    private static async Task WaitForUserLocksAsync(CheckoutFixture f, int minimum)
    {
        DateTime limit = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < limit)
        {
            await using var command = f.Database.Data.CreateCommand("SELECT count(*) FROM pg_stat_activity a WHERE a.datname=current_database() AND a.wait_event_type='Lock' AND EXISTS(SELECT 1 FROM pg_locks l WHERE l.pid=a.pid AND l.relation='shop.users'::regclass AND l.mode='RowShareLock')");
            if ((long)(await command.ExecuteScalarAsync(Ct) ?? 0L) >= minimum) return;
            await Task.Delay(50, Ct);
        }
        throw new TimeoutException("Synthetic user-lock contention was not observed.");
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
