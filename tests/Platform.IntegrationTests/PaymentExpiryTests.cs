using Platform.Core;
using Platform.Payments;
using Platform.Persistence;
using Platform.Security;
using Platform.Telegram;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class PaymentExpiryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task CanonicalPaymentDeadlineAndMembershipGuardsNeedNoDuplicateMigration() => await PaymentDeadlineQualification.SchemaAsync(Ct);
    [Fact]
    public async Task ExpiryReplaysOnceReleasesWholeClaimAndKeepsOriginalInvoiceAndQuote() => await PaymentDeadlineQualification.ExpiryAsync(Ct);
    [Fact]
    public async Task ExistingEvidenceAndReviewObligationsCannotExpireOrDonateTheirClaims() => await PaymentDeadlineQualification.ProtectionAsync(Ct);
    [Fact]
    public async Task StatusEditsRecheckCurrentPaymentAndNeverExpireOrOverwritePaidOrders() => await PaymentMessageQualification.ProjectionAsync(Ct);
    [Fact]
    public async Task RateLimitsAndAuditedFallbackRetainSavedTargetsAndKnownLateReceipts() => await PaymentMessageQualification.RecoveryAsync(Ct);
    [Fact]
    public async Task UnknownNewMessagePausesUntilLateKnownSuccessWithoutAnAutomaticResend() => await PaymentMessageQualification.TimeoutAsync(Ct);
    [Fact]
    public async Task ExpiredMessageLeasesRetainUnknownSendsAndRecoverOnlyTheOriginalEdit() => await PaymentMessageQualification.LeaseRecoveryAsync(Ct);
    [Fact]
    public async Task PaymentStatusCannotOvertakeOrEraseTheAuthorizedCompleteCredentialManifest() => await PaymentMessageQualification.CredentialOwnershipAsync(Ct);
    [Fact]
    public async Task UncertainOldStatusEditRequiresExplicitCredentialRecoveryToANewSafeMessage() => await PaymentMessageQualification.CredentialOwnershipAsync(Ct, uncertain: true);
    [Fact]
    public async Task ReviewObligationCanCoexistWithOneIndependentPendingOrder()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        var payments = new PaymentSettlementFixture(f); await payments.EnableAsync(Ct);
        var old = await payments.OrderAsync(ct: Ct);
        await payments.CaptureAsync(old.Payment, old.Origin, old.Due, ct: Ct);
        var next = await payments.OrderAsync(ct: Ct);
        Assert.NotEqual(old.Order, next.Order);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='confirmation_pending' AND resolution_kind IS NULL", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='awaiting_payment'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", Ct));
    }
    [Theory]
    [InlineData(CheckoutPayment.Crypto, CheckoutPayment.Bank)]
    [InlineData(CheckoutPayment.Bank, CheckoutPayment.Crypto)]
    public async Task SwitchingPaymentMethodCancelsOldAwaitingOrderAndReleasesWholeHeldCouponClaim(CheckoutPayment firstMethod, CheckoutPayment secondMethod)
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        var payments = new PaymentSettlementFixture(f);
        await payments.EnableAsync(Ct);
        await payments.EnableBankAsync(Ct);
        Guid coupon = Guid.NewGuid();
        await using (var issue = await f.Sessions.BeginAsync(Ct))
        {
            await new PricingStore(f.Sessions, f.Secrets).IssueAsync(issue, f.Context(0), await f.AdminAsync(Ct), new(coupon, "PUBLIC-SWITCH-COUPON", CouponKind.Promotion, 50, f.Product, f.Users[0], MaximumUses: 2), Guid.NewGuid(), Ct);
            await issue.CommitAsync(Ct);
        }
        var service = new WalletCheckoutService(f.Sessions, f.Store, payments.Catalog);
        var firstReq = new CheckoutRequest(OrderIds.New(), f.Product, 1, firstMethod);
        var firstRes = await service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), firstReq, (_, _, _) => Task.FromResult(ChannelMembership.Member), Ct);
        Assert.Equal(CheckoutOutcome.AwaitingPayment, firstRes.Outcome);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='awaiting_payment'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='held'", Ct));
        var secondReq = new CheckoutRequest(OrderIds.New(), f.Product, 1, secondMethod);
        var secondRes = await service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), secondReq, (_, _, _) => Task.FromResult(ChannelMembership.Member), Ct);
        Assert.Equal(CheckoutOutcome.AwaitingPayment, secondRes.Outcome);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='awaiting_payment'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='cancelled' AND resolution_kind='replaced'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='held'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='released'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", Ct));
    }
    [Fact]
    public async Task InvalidReplacementLeavesOriginalOrderAndTimerUntouched()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        var payments = new PaymentSettlementFixture(f);
        await payments.EnableAsync(Ct);
        var first = await payments.OrderAsync(ct: Ct);
        var service = new WalletCheckoutService(f.Sessions, f.Store, payments.Catalog);
        var badReq = new CheckoutRequest(OrderIds.New(), Guid.NewGuid(), 1, CheckoutPayment.Crypto);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), badReq, (_, _, _) => Task.FromResult(ChannelMembership.Member), Ct));
        await using var read = await f.Sessions.BeginAsync(Ct);
        var original = await read.QuerySingleAsync(new DbStatement("SELECT payment_due_at=created_at+INTERVAL '30 minutes' AND resolution_kind IS NULL FROM shop.orders WHERE id=$1", DbValue.Uuid(first.Order)), r => r.GetBoolean(0), Ct);
        Assert.True(original);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='awaiting_payment'", Ct));
    }
    [Fact]
    public async Task OnlyOneAwaitingOrderPerBrandMembershipRejectedByUniqueIndex()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        var payments = new PaymentSettlementFixture(f); await payments.EnableAsync(Ct);
        var first = await payments.OrderAsync(ct: Ct);
        await using var invalid = await f.Sessions.BeginAsync(Ct);
        var err = await Assert.ThrowsAsync<DbOperationException>(() => invalid.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.orders(id,user_id,product_id,quantity,order_kind,payment_status,currency,payment_method,unit_price_usdt,
                auto_price_toman_snapshot,pricing_rounding_step_snapshot,price_calculation_version,brand_public_name_snapshot,
                product_title_snapshot,payment_terms_snapshot,fulfillment_kind_snapshot,local_first_snapshot,is_preorder,payment_due_at)
            VALUES (gen_random_uuid(),$1,$2,1,'product_purchase','awaiting_payment','USDT','crypto',10,false,5000,$3,'public brand','public title','public terms','local',true,false,NOW()+INTERVAL '30 minutes')
            """, DbValue.Uuid(f.Users[0]), DbValue.Uuid(f.Product), DbValue.Text(PricingPolicy.Version)), Ct));
        Assert.Equal("23505", err.Failure.SqlState);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='awaiting_payment'", Ct));
    }
    [Fact]
    public async Task SupportedCryptoNetworksAreExactlyUsdtTrc20AndUsdtBsc()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        var payments = new PaymentSettlementFixture(f); await payments.EnableAsync(Ct);
        var order = await payments.OrderAsync(ct: Ct);
        await using var read = await f.Sessions.BeginAsync(Ct);
        string asset = await read.QuerySingleAsync(new DbStatement("SELECT network_asset FROM shop.payments WHERE order_id=$1", DbValue.Uuid(order.Order)), r => r.GetString(0), Ct);
        Assert.Equal("USDTTRC20", asset);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM pg_constraint WHERE conrelid='shop.payments'::regclass AND pg_get_constraintdef(oid) LIKE '%USDTTRC20%' AND pg_get_constraintdef(oid) LIKE '%USDTBSC%'", Ct));
    }
    [Theory]
    [InlineData("USDTTRC20")]
    [InlineData("USDTBSC")]
    public async Task AcceptedNetworkAssetsMatchConstraintExactly(string asset)
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        await using var valid = await f.Sessions.BeginAsync(Ct);
        await valid.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.payments(id,topup_user_id,payment_method,quote_currency,amount_due,idempotency_ref,network_asset)
            VALUES (gen_random_uuid(),$1,'crypto','USD',5,$2,$3)
            """, DbValue.Uuid(f.Users[0]), DbValue.Text("topup-asset-" + asset), DbValue.Text(asset)), Ct);
        await valid.CommitAsync(Ct);
        await using var read = await f.Sessions.BeginAsync(Ct);
        Assert.Equal(1L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.payments WHERE network_asset=$1", DbValue.Text(asset)), r => r.GetInt64(0), Ct));
    }
    [Fact]
    public async Task RejectedNetworkAssetBlocksPaymentRow()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        await using var invalid = await f.Sessions.BeginAsync(Ct);
        var err = await Assert.ThrowsAsync<DbOperationException>(() => invalid.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.payments(id,topup_user_id,payment_method,quote_currency,amount_due,idempotency_ref,network_asset)
            VALUES (gen_random_uuid(),$1,'crypto','USD',5,'topup-bad-asset','TRX')
            """, DbValue.Uuid(f.Users[0])), Ct));
        Assert.Equal(DbFailureKind.Constraint, err.Failure.Kind);
    }
    [Fact]
    public async Task StandaloneTopupRequiresCryptoMethodNotBank()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        await using var invalid = await f.Sessions.BeginAsync(Ct);
        var err = await Assert.ThrowsAsync<DbOperationException>(() => invalid.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.payments(id,topup_user_id,payment_method,quote_currency,amount_due,idempotency_ref,network_asset,bank_destination_ciphertext,bank_destination_key_id)
            VALUES (gen_random_uuid(),$1,'bank','TOMAN',1000000,'topup-bank-bad',NULL,$2,'current')
            """, DbValue.Uuid(f.Users[0]), DbValue.Binary([1, 2, 3])), Ct));
        Assert.Equal(DbFailureKind.Constraint, err.Failure.Kind);
    }
    [Fact]
    public async Task WalletAndLedgerHaveNoCurrencyColumnAndOrdersHaveNoRedundantFields()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        await using var read = await f.Sessions.BeginAsync(Ct);
        long forbidden = await read.QuerySingleAsync(new DbStatement("""
            SELECT count(*) FROM information_schema.columns WHERE table_schema='shop' AND
                ((table_name IN ('orders','payments') AND column_name IN ('brand_id','telegram_id')) OR
                 (table_name IN ('wallets','wallet_entries') AND column_name='currency') OR
                 (table_name='orders' AND column_name='payable_usd') OR
                 data_type IN ('json','jsonb','ARRAY','real','double precision'))
            """), r => r.GetInt64(0), Ct);
        Assert.Equal(0L, forbidden);
    }
    [Fact]
    public async Task DistinctIranianBrandCardsRejectDuplicateNumberAcrossActiveIranBrands()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        var payments = new PaymentSettlementFixture(f);
        await payments.EnableBankAsync(Ct);
        var admin = await f.AdminAsync(Ct);
        await using (var edit = await f.Sessions.BeginAsync(Ct))
        {
            await edit.ExecuteAsync(new DbStatement("UPDATE shop.brands SET market='iran',policy_version=policy_version+1 WHERE id=$1", DbValue.Uuid(f.Brands[1])), Ct);
            await edit.CommitAsync(Ct);
        }
        long revision;
        await using (var read = await f.Sessions.BeginAsync(Ct)) revision = await read.QuerySingleAsync(new DbStatement("SELECT policy_version FROM shop.brands WHERE id=$1", DbValue.Uuid(f.Brands[1])), r => r.GetInt64(0), Ct);
        using var bank = new BankDestinationService(f.Sessions, f.Secrets, new byte[32]);
        var err = await Assert.ThrowsAsync<DbOperationException>(() => bank.SaveAsync(f.Context(1), admin, "1111222233334444", "Public second holder", revision, Guid.NewGuid(), Ct));
        Assert.Equal(DbFailureKind.Constraint, err.Failure.Kind);
    }
    [Fact]
    public async Task CryptoAndBankPaymentsAreTypedAndOriginalMethodIsImmutable()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        var payments = new PaymentSettlementFixture(f);
        await payments.EnableAsync(Ct);
        await payments.EnableBankAsync(Ct);
        var order = await payments.OrderAsync(ct: Ct);
        await using var invalid = await f.Sessions.BeginAsync(Ct);
        var err = await Assert.ThrowsAsync<DbOperationException>(() => invalid.ExecuteAsync(new DbStatement("UPDATE shop.payments SET payment_method='bank' WHERE id=$1", DbValue.Uuid(order.Payment)), Ct));
        Assert.Equal(DbFailureKind.Constraint, err.Failure.Kind);
    }
    [Fact]
    public async Task PaymentDueAtIsStrictlyImmutableAndCannotBeExtended()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        var payments = new PaymentSettlementFixture(f); await payments.EnableAsync(Ct);
        var order = await payments.OrderAsync(ct: Ct);
        await using var invalid = await f.Sessions.BeginAsync(Ct);
        var err = await Assert.ThrowsAsync<DbOperationException>(() => invalid.ExecuteAsync(new DbStatement("UPDATE shop.orders SET payment_due_at=payment_due_at+INTERVAL '1 minute' WHERE id=$1", DbValue.Uuid(order.Order)), Ct));
        Assert.Equal(DbFailureKind.Constraint, err.Failure.Kind);
    }
    [Fact]
    public async Task SingleAwaitingOrderPerMembershipAppliesAcrossDifferentPaymentMethods()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        var payments = new PaymentSettlementFixture(f);
        await payments.EnableAsync(Ct);
        await payments.EnableBankAsync(Ct);
        var cryptoOrder = await payments.OrderAsync(ct: Ct);
        await using var invalid = await f.Sessions.BeginAsync(Ct);
        var err = await Assert.ThrowsAsync<DbOperationException>(() => invalid.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.orders(id,user_id,product_id,quantity,order_kind,payment_status,currency,payment_method,unit_price_usdt,
                unit_price_toman,auto_price_toman_snapshot,pricing_rounding_step_snapshot,price_calculation_version,brand_public_name_snapshot,
                product_title_snapshot,payment_terms_snapshot,fulfillment_kind_snapshot,local_first_snapshot,is_preorder,payment_due_at)
            VALUES (gen_random_uuid(),$1,$2,1,'product_purchase','awaiting_payment','TOMAN','bank',10,1000000,false,5000,$3,'public brand','public title','public terms','local',true,false,NOW()+INTERVAL '30 minutes')
            """, DbValue.Uuid(f.Users[0]), DbValue.Uuid(f.Product), DbValue.Text(PricingPolicy.Version)), Ct));
        Assert.Equal("23505", err.Failure.SqlState);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='awaiting_payment'", Ct));
    }
}
