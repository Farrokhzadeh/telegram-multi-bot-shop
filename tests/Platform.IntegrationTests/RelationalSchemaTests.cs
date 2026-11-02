using Npgsql;
using Platform.Persistence;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class RelationalSchemaTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BaselineCreatesExactly28TablesAndReapplicationVerifiesWithoutWriting()
    {
        await using var database = await DisposablePostgres.CreateAsync(Ct);
        var manifest = SchemaManifest.ForQualification(database.Data.ConnectionString);
        var applied = await SchemaRunner.ApplyAsync(database.Data, manifest, Ct);
        var repeated = await SchemaRunner.ApplyAsync(database.Data, manifest, Ct);
        Assert.True(applied.Applied);
        Assert.False(repeated.Applied);
        Assert.Equal(applied.CatalogSha256, repeated.CatalogSha256);
        Assert.Equal(28L, await ScalarAsync<long>(database, "SELECT count(*) FROM pg_tables WHERE schemaname='shop'"));
        Assert.Equal(0L, await ScalarAsync<long>(database,
            "SELECT count(*) FROM information_schema.columns WHERE table_schema='shop' AND (data_type IN ('json','jsonb','ARRAY','real','double precision') OR column_name IN ('public_ref','total_usdt','total_toman','paid_at','payload','settings'))"));
    }

    [Theory]
    [InlineData("ALTER TABLE shop.orders ADD COLUMN unreviewed text")]
    [InlineData("ALTER TABLE shop.orders DROP CONSTRAINT orders_quantity_check")]
    [InlineData("ALTER TABLE shop.orders DISABLE TRIGGER orders_guard")]
    [InlineData("CREATE TABLE shop.unapproved_history (id integer)")]
    [InlineData("CREATE TYPE shop.unreviewed AS ENUM ('unreviewed')")]
    [InlineData("COMMENT ON SCHEMA shop IS 'unreviewed'")]
    public async Task CatalogOrMarkerDriftFailsWithoutSilentlyRepairing(string mutation)
    {
        await using var database = await CreateAsync();
        await ExecuteAsync(database, mutation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaRunner.ApplyAsync(database.Data,
            SchemaManifest.ForQualification(database.Data.ConnectionString), Ct));
    }

    [Fact]
    public async Task PartialSchemaAndWrongManifestAreRejectedBeforeAnyBaselineWrites()
    {
        await using var database = await DisposablePostgres.CreateAsync(Ct);
        var manifest = SchemaManifest.ForQualification(database.Data.ConnectionString);
        await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaRunner.ApplyAsync(database.Data,
            manifest with { SqlSha256 = new string('0', 64) }, Ct));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM pg_namespace WHERE nspname='shop'"));
        await ExecuteAsync(database, "CREATE SCHEMA shop");
        await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaRunner.ApplyAsync(database.Data, manifest, Ct));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM pg_tables WHERE schemaname='shop'"));
    }

    [Fact]
    public async Task ConcurrentOperatorsApplyOnceThenAgreeOnTheCatalog()
    {
        await using var database = await DisposablePostgres.CreateAsync(Ct);
        var manifest = SchemaManifest.ForQualification(database.Data.ConnectionString);
        SchemaReceipt[] results = await Task.WhenAll(Enumerable.Range(0, 2)
            .Select(_ => SchemaRunner.ApplyAsync(database.Data, manifest, Ct)));
        Assert.Single(results, r => r.Applied);
        Assert.Single(results.Select(r => r.CatalogSha256).Distinct());
    }

    [Fact]
    public async Task OuterTransactionRollbackRemovesAllBaselineObjects()
    {
        await using var database = await DisposablePostgres.CreateAsync(Ct);
        await using var connection = await database.Data.OpenConnectionAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using var command = new NpgsqlCommand(SchemaRunner.BaselineSql, connection, transaction);
        await command.ExecuteNonQueryAsync(Ct);
        await transaction.RollbackAsync(Ct);
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM pg_namespace WHERE nspname='shop'"));
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public async Task EveryNonFiniteNumericIsRejected(string value)
    {
        await using var database = await CreateAsync();
        await using var command = database.Data.CreateCommand("UPDATE shop.brand_products SET price_usdt=CAST(@value AS numeric)");
        command.Parameters.AddWithValue("value", value);
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Fact]
    public async Task NumericStoragePreservesExactHighPrecisionWithoutScaleTruncation()
    {
        await using var database = await CreateAsync();
        const string amount = "123456789012.123456789012";
        await ExecuteAsync(database, "UPDATE shop.brand_products SET price_usdt=" + amount);
        Assert.Equal(amount, await ScalarAsync<string>(database,
            "SELECT price_usdt::text FROM shop.brand_products ORDER BY brand_id,product_id LIMIT 1"));
    }

    [Fact]
    public async Task MembershipReferencesCannotCrossBrandOrAnotherMemberOfTheSameBrand()
    {
        await using var database = await CreateAsync();
        await RejectAsync(database, "UPDATE shop.users SET flow_order_id='00000000-0000-0000-0000-000000000402',flow_revision=1 WHERE id='00000000-0000-0000-0000-000000000101'");
        await RejectAsync(database, "INSERT INTO shop.support_tickets(id,user_id,order_id,category,source) VALUES ('00000000-0000-0000-0000-000000000701','00000000-0000-0000-0000-000000000103','00000000-0000-0000-0000-000000000401','order','customer')");
        await RejectAsync(database, "UPDATE shop.orders SET user_id='00000000-0000-0000-0000-000000000102' WHERE id='00000000-0000-0000-0000-000000000401'");
        await RejectAsync(database, "INSERT INTO shop.product_stock_subscriptions(id,user_id,product_id) VALUES ('00000000-0000-0000-0000-000000000702','00000000-0000-0000-0000-000000000102','00000000-0000-0000-0000-000000000202')");
    }

    [Fact]
    public async Task OnlyOneAwaitingOrderPerMembershipAndDuplicateRemotePaymentIsRejected()
    {
        await using var database = await CreateAsync();
        await RejectAsync(database, OrderSql("00000000-0000-0000-0000-000000000411", UserA), PostgresErrorCodes.UniqueViolation);
        await ExecuteAsync(database, """
            INSERT INTO shop.payments(id,order_id,payment_method,quote_currency,amount_due,idempotency_ref,network_asset,provider_code,provider_payment_ref)
            VALUES ('00000000-0000-0000-0000-000000000501','00000000-0000-0000-0000-000000000401','crypto','USD',20,'public-test-payment-1','USDTTRC20','synthetic','original-payment');
            """);
        await RejectAsync(database, """
            INSERT INTO shop.payments(id,order_id,payment_method,quote_currency,amount_due,idempotency_ref,network_asset,provider_code,provider_payment_ref)
            VALUES ('00000000-0000-0000-0000-000000000502','00000000-0000-0000-0000-000000000402','crypto','USD',20,'public-test-payment-2','USDTBSC','synthetic','original-payment')
            """, PostgresErrorCodes.UniqueViolation);
    }

    [Theory]
    [InlineData("NULL,NULL,'crypto','USD','USDTBSC',NULL,NULL")]
    [InlineData("'00000000-0000-0000-0000-000000000401','00000000-0000-0000-0000-000000000101','crypto','USD','USDTBSC',NULL,NULL")]
    [InlineData("NULL,'00000000-0000-0000-0000-000000000101','bank','TOMAN',NULL,decode('01','hex'),'public-test-key'")]
    [InlineData("NULL,'00000000-0000-0000-0000-000000000101','crypto','USD','BTC',NULL,NULL")]
    public async Task PaymentXorAndPermittedMethodAssetCombinationsAreEnforced(string values)
    {
        await using var database = await CreateAsync();
        await RejectAsync(database, "INSERT INTO shop.payments(order_id,topup_user_id,payment_method,quote_currency,network_asset,bank_destination_ciphertext,bank_destination_key_id,id,amount_due,idempotency_ref) VALUES (" + values + ",gen_random_uuid(),20,'public-test-invalid-payment')");
    }

    [Fact]
    public async Task StandaloneCryptoFundingDoesNotRequireAnExistingWallet()
    {
        await using var database = await CreateAsync();
        await ExecuteAsync(database, """
            INSERT INTO shop.payments(id,topup_user_id,payment_method,quote_currency,amount_due,idempotency_ref,network_asset)
            VALUES ('00000000-0000-0000-0000-000000000501','00000000-0000-0000-0000-000000000101','crypto','USD',20,'public-test-topup','USDTBSC')
            """);
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM shop.wallets"));
    }

    [Fact]
    public async Task SharedProductJobsCarryNoGuessedBrandAndOtherJobsRequireExactOrigins()
    {
        await using var database = await CreateAsync();
        await ExecuteAsync(database, """
            INSERT INTO shop.jobs(id,job_kind,product_id,dedupe_key) VALUES (gen_random_uuid(),'evaluate_restock','00000000-0000-0000-0000-000000000201','public-shared-job');
            INSERT INTO shop.jobs(id,job_kind,brand_id,dedupe_key) VALUES (gen_random_uuid(),'refresh_fx','00000000-0000-0000-0000-000000000001','public-fx-job');
            INSERT INTO shop.jobs(id,job_kind,dedupe_key) VALUES (gen_random_uuid(),'activity_cleanup','public-cleanup-job')
            """);
        await RejectAsync(database, "INSERT INTO shop.jobs(id,job_kind,brand_id,product_id,dedupe_key) VALUES (gen_random_uuid(),'evaluate_restock','00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000201','public-invalid-shared-job')");
        await RejectAsync(database, "INSERT INTO shop.jobs(id,job_kind,brand_id,order_id,dedupe_key) VALUES (gen_random_uuid(),'prepare_manifest','00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000401','public-invalid-brand-job')");
        await RejectAsync(database, "INSERT INTO shop.jobs(id,job_kind,brand_id,order_id,product_id,dedupe_key) VALUES (gen_random_uuid(),'prepare_manifest','00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000401','00000000-0000-0000-0000-000000000201','public-extra-origin')");
    }

    [Fact]
    public async Task IncompleteManifestRollsBackAndCompleteManifestCanReleaseTogether()
    {
        await using var database = await CreateAsync();
        await RejectAsync(database, UnitSql(1) + ManifestSql);
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM shop.fulfillments"));
        await ExecuteTransactionAsync(database, UnitSql(1) + UnitSql(2) + ManifestSql);
        Assert.Equal(2L, await ScalarAsync<long>(database, "SELECT count(*) FROM shop.fulfillments WHERE status='ready'"));
        await RejectAsync(database, "UPDATE shop.orders SET manifest_asset_ref='changed' WHERE id='00000000-0000-0000-0000-000000000403'");
    }

    [Fact]
    public async Task SavedOrderAnchorCannotBelongToAnotherMemberOrOrder()
    {
        await using var database = await CreateAsync();
        await ExecuteTransactionAsync(database, UnitSql(1) + UnitSql(2) + ManifestSql);
        await ExecuteAsync(database, """
            INSERT INTO shop.telegram_deliveries(id,brand_id,recipient_kind,user_id,purpose,action,dedupe_key,order_id,status,telegram_message_id,sent_at)
            VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000001','customer_dm','00000000-0000-0000-0000-000000000101','order_status','send','public-anchor-other-order','00000000-0000-0000-0000-000000000401','succeeded',99,CURRENT_TIMESTAMP)
            """);
        const string delivery = """
            INSERT INTO shop.telegram_deliveries(id,brand_id,recipient_kind,user_id,purpose,action,target_message_id,dedupe_key,order_id,delivery_generation)
            VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000001','customer_dm','00000000-0000-0000-0000-000000000103','order_credentials','edit',99,'public-credentials','00000000-0000-0000-0000-000000000403','00000000-0000-0000-0000-000000000801')
            """;
        await RejectAsync(database, delivery);
        await ExecuteAsync(database, """
            INSERT INTO shop.telegram_deliveries(id,brand_id,recipient_kind,user_id,purpose,action,dedupe_key,order_id,status,telegram_message_id,sent_at)
            VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000001','customer_dm','00000000-0000-0000-0000-000000000103','order_status','send','public-anchor-valid','00000000-0000-0000-0000-000000000403','succeeded',100,CURRENT_TIMESTAMP)
            """);
        await ExecuteAsync(database, delivery.Replace("'edit',99", "'edit',100", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SameTelegramUserAcrossBrandsIsTwoMembershipsAndConsentIsUniqueWhileOpen()
    {
        await using var database = await CreateAsync();
        Assert.Equal(2L, await ScalarAsync<long>(database, "SELECT count(*) FROM shop.users WHERE telegram_id=7001"));
        const string consent = "INSERT INTO shop.product_stock_subscriptions(id,user_id,product_id) VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000101','00000000-0000-0000-0000-000000000201')";
        await ExecuteAsync(database, consent);
        await RejectAsync(database, consent, PostgresErrorCodes.UniqueViolation);
        await ExecuteAsync(database, "UPDATE shop.product_stock_subscriptions SET cancelled_at=CURRENT_TIMESTAMP,closed_at=CURRENT_TIMESTAMP");
        await ExecuteAsync(database, consent);
        Assert.Equal(2L, await ScalarAsync<long>(database, "SELECT count(*) FROM shop.product_stock_subscriptions"));
    }

    [Fact]
    public async Task WalletMutationRequiresAnExactImmutablePostingInTheSameTransaction()
    {
        await using var database = await CreateAsync();
        await ExecuteAsync(database, "INSERT INTO shop.wallets(user_id) VALUES ('00000000-0000-0000-0000-000000000101')");
        await RejectAsync(database, "UPDATE shop.wallets SET balance=10,version=1");
        await ExecuteTransactionAsync(database, """
            INSERT INTO shop.audit_events(id,actor_kind,actor_admin_id,brand_id,action,entity_kind,entity_id,reason,correlation_id)
            VALUES ('00000000-0000-0000-0000-000000000901','admin','00000000-0000-0000-0000-000000000301','00000000-0000-0000-0000-000000000001','wallet_adjustment','wallet','00000000-0000-0000-0000-000000000101','public synthetic adjustment',gen_random_uuid());
            UPDATE shop.wallets SET balance=10,version=1;
            INSERT INTO shop.wallet_entries(id,user_id,signed_amount,balance_after,posting_version,movement_kind,is_posting,idempotency_key,admin_id,audit_event_id,reason)
            VALUES ('00000000-0000-0000-0000-000000000902','00000000-0000-0000-0000-000000000101',10,10,1,'admin_adjustment',true,'public-ledger-adjustment','00000000-0000-0000-0000-000000000301','00000000-0000-0000-0000-000000000901','public synthetic adjustment')
            """);
        Assert.Equal(10m, await ScalarAsync<decimal>(database, "SELECT balance FROM shop.wallets"));
        await RejectAsync(database, "UPDATE shop.wallet_entries SET signed_amount=11");
        await RejectAsync(database, "DELETE FROM shop.wallet_entries");
        await RejectAsync(database, "TRUNCATE shop.audit_events CASCADE");
    }

    [Fact]
    public async Task SupplierAttemptCeilingIsInclusiveAndUnknownSubmitCannotBeRetriedBlindly()
    {
        await using var database = await CreateAsync();
        await SeedSupplierAsync(database);
        await RejectAsync(database, AttemptSql(1, "submit", "11.000000000000000001", "NULL"));
        await ExecuteAsync(database, AttemptSql(1, "submit", "11", "NULL"));
        await ExecuteAsync(database, "UPDATE shop.supplier_request_attempts SET outcome='unknown',finished_at=CURRENT_TIMESTAMP");
        await ExecuteAsync(database, """
            UPDATE shop.supplier_requests SET status='unknown';
            INSERT INTO shop.wallets(user_id) VALUES ('00000000-0000-0000-0000-000000000101');
            INSERT INTO shop.audit_events(id,actor_kind,actor_admin_id,brand_id,action,entity_kind,entity_id,reason,correlation_id)
            VALUES ('00000000-0000-0000-0000-000000000903','admin','00000000-0000-0000-0000-000000000301','00000000-0000-0000-0000-000000000001',
                'supplier_credit','supplier_request','00000000-0000-0000-0000-000000000602','public unresolved-credit rejection',gen_random_uuid())
            """);
        await RejectAsync(database, """
            UPDATE shop.wallets SET balance=10,version=1 WHERE user_id='00000000-0000-0000-0000-000000000101';
            INSERT INTO shop.wallet_entries(id,user_id,signed_amount,balance_after,posting_version,movement_kind,is_posting,idempotency_key,order_id,supplier_request_id,admin_id,audit_event_id,reason)
            VALUES ('00000000-0000-0000-0000-000000000904','00000000-0000-0000-0000-000000000101',10,10,1,'supplier_wallet_credit',true,'public-unknown-credit',
                '00000000-0000-0000-0000-000000000404','00000000-0000-0000-0000-000000000602','00000000-0000-0000-0000-000000000301','00000000-0000-0000-0000-000000000903','public unresolved-credit rejection');
            UPDATE shop.orders SET fulfillment_status='compensated',resolution_kind='supplier_wallet_credit',resolution_admin_id='00000000-0000-0000-0000-000000000301',
                resolution_audit_event_id='00000000-0000-0000-0000-000000000903',resolution_at=CURRENT_TIMESTAMP,resolution_wallet_entry_id='00000000-0000-0000-0000-000000000904'
            WHERE id='00000000-0000-0000-0000-000000000404'
            """);
        await RejectAsync(database, AttemptSql(2, "submit", "11", "NULL"));
        string submitId = await ScalarAsync<string>(database, "SELECT id::text FROM shop.supplier_request_attempts WHERE attempt_no=1");
        await ExecuteAsync(database, AttemptSql(2, "status_lookup", "NULL", "'" + submitId + "'"));
        await RejectAsync(database, "UPDATE shop.supplier_requests SET cost_baseline_amount=12");
        await RejectAsync(database, "UPDATE shop.supplier_request_attempts SET outcome='failed' WHERE attempt_no=1");
    }

    [Fact]
    public async Task SupplierBonusesAndCustomerUnitsCannotClaimTheSameStableItem()
    {
        await using var database = await CreateAsync();
        await SeedSupplierAsync(database);
        await ExecuteAsync(database, """
            INSERT INTO shop.fulfillments(id,order_id,unit_no,kind,status,supplier_request_id,supplier_item_key,delivered_content,delivered_content_key_id,content_kind)
            VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000404',1,'supplier','acquired','00000000-0000-0000-0000-000000000602',decode(repeat('22',32),'hex'),decode('01','hex'),'public-test-key','code')
            """);
        const string bonus = """
            INSERT INTO shop.inventory_items(id,product_id,credential_ciphertext,credential_key_id,source_fingerprint,origin_kind,supplier_request_id,supplier_item_key,state)
            VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000203',decode('01','hex'),'public-test-key',decode(repeat('33',32),'hex'),
                'supplier_bonus','00000000-0000-0000-0000-000000000602',decode(repeat('22',32),'hex'),'admin_held')
            """;
        await RejectAsync(database, bonus);
        await ExecuteAsync(database, bonus.Replace("repeat('22',32)", "repeat('44',32)", StringComparison.Ordinal));
        await RejectAsync(database, "UPDATE shop.inventory_items SET state='available' WHERE origin_kind='supplier_bonus'");
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT count(*) FROM shop.inventory_items WHERE state='admin_held'"));
    }

    [Fact]
    public async Task PreorderAllocationIsWholeAndHeldHeadCannotBeBypassed()
    {
        await using var database = await CreateAsync();
        const string product = "00000000-0000-0000-0000-000000000202";
        await ExecuteTransactionAsync(database, OrderSql("00000000-0000-0000-0000-000000000404", UserA, true, product, "local", true) +
            OrderSql("00000000-0000-0000-0000-000000000405", UserA2, true, product, "local", true, 1) + """
            UPDATE shop.orders SET fulfillment_status='waiting_stock' WHERE id IN ('00000000-0000-0000-0000-000000000404','00000000-0000-0000-0000-000000000405');
            INSERT INTO shop.fulfillments(id,order_id,unit_no,kind) VALUES
                ('00000000-0000-0000-0000-000000000611','00000000-0000-0000-0000-000000000404',1,'local'),
                ('00000000-0000-0000-0000-000000000612','00000000-0000-0000-0000-000000000404',2,'local'),
                ('00000000-0000-0000-0000-000000000613','00000000-0000-0000-0000-000000000405',1,'local');
            """);
        string Allocation(string unit, string fingerprint) => $$"""
            INSERT INTO shop.inventory_items(id,product_id,credential_ciphertext,credential_key_id,source_fingerprint,origin_kind,state,assigned_fulfillment_id,allocated_at)
            VALUES (gen_random_uuid(),'{{product}}',decode('01','hex'),'public-test-key',decode(repeat('{{fingerprint}}',32),'hex'),
                'local_import','allocated','{{unit}}',CURRENT_TIMESTAMP);
            """;
        await RejectAsync(database, Allocation("00000000-0000-0000-0000-000000000611", "11"));
        await RejectAsync(database, "UPDATE shop.orders SET fulfillment_status='acquiring' WHERE id='00000000-0000-0000-0000-000000000404'");
        await ExecuteAsync(database, "UPDATE shop.orders SET hold_at=CURRENT_TIMESTAMP,hold_admin_id='00000000-0000-0000-0000-000000000301',hold_reason='public test hold' WHERE id='00000000-0000-0000-0000-000000000404'");
        await RejectAsync(database, Allocation("00000000-0000-0000-0000-000000000613", "33"));
        await ExecuteAsync(database, "UPDATE shop.orders SET hold_at=NULL,hold_admin_id=NULL,hold_reason=NULL WHERE id='00000000-0000-0000-0000-000000000404'");
        await ExecuteTransactionAsync(database, Allocation("00000000-0000-0000-0000-000000000611", "11") + Allocation("00000000-0000-0000-0000-000000000612", "22"));
        Assert.Equal(2L, await ScalarAsync<long>(database, "SELECT count(*) FROM shop.inventory_items WHERE state='allocated'"));
    }

    [Fact]
    public async Task OwnerCanReduceCouponCapacityWhileExistingClaimKeepsItsGrant()
    {
        await using var database = await CreateAsync();
        await ExecuteTransactionAsync(database, """
            INSERT INTO shop.discounts(id,brand_id,code_digest,code_ciphertext,code_key_id,display_prefix,issuance_batch_id,kind,percent_off,max_total_uses,created_by_admin_id)
            VALUES ('00000000-0000-0000-0000-000000000621','00000000-0000-0000-0000-000000000001',decode(repeat('55',32),'hex'),decode('01','hex'),'public-test-key','PUB',gen_random_uuid(),'promotion',10,2,'00000000-0000-0000-0000-000000000301');
            INSERT INTO shop.audit_events(id,actor_kind,actor_admin_id,brand_id,action,entity_kind,entity_id,reason,new_revision,correlation_id)
            VALUES ('00000000-0000-0000-0000-000000000622','admin','00000000-0000-0000-0000-000000000301','00000000-0000-0000-0000-000000000001','coupon_issue','discount','00000000-0000-0000-0000-000000000621','public coupon issue',0,gen_random_uuid());
            INSERT INTO shop.discount_revisions(id,discount_id,revision_no,audit_event_id,percent_off,max_total_uses,usage_adjustment_units,redeemed_count_at_revision,held_units_at_revision,active)
            VALUES ('00000000-0000-0000-0000-000000000623','00000000-0000-0000-0000-000000000621',0,'00000000-0000-0000-0000-000000000622',10,2,0,0,0,true);
            INSERT INTO shop.discount_uses(discount_id,order_id,discount_revision_id,units_covered,saved_amount,saved_currency,status)
            VALUES ('00000000-0000-0000-0000-000000000621','00000000-0000-0000-0000-000000000401','00000000-0000-0000-0000-000000000623',2,2,'USD','held');
            UPDATE shop.discounts SET max_total_uses=0,usage_adjustment_units=50,active=false,revision=1 WHERE id='00000000-0000-0000-0000-000000000621';
            INSERT INTO shop.audit_events(id,actor_kind,actor_admin_id,brand_id,action,entity_kind,entity_id,reason,new_revision,correlation_id)
            VALUES ('00000000-0000-0000-0000-000000000624','admin','00000000-0000-0000-0000-000000000301','00000000-0000-0000-0000-000000000001','coupon_edit','discount','00000000-0000-0000-0000-000000000621','public owner reduction',1,gen_random_uuid());
            INSERT INTO shop.discount_revisions(id,discount_id,revision_no,audit_event_id,percent_off,max_total_uses,usage_adjustment_units,redeemed_count_at_revision,held_units_at_revision,active)
            VALUES ('00000000-0000-0000-0000-000000000625','00000000-0000-0000-0000-000000000621',1,'00000000-0000-0000-0000-000000000624',10,0,50,0,2,false)
            """);
        await RejectAsync(database, """
            INSERT INTO shop.discount_uses(discount_id,order_id,discount_revision_id,units_covered,saved_amount,saved_currency,status)
            VALUES ('00000000-0000-0000-0000-000000000621','00000000-0000-0000-0000-000000000402','00000000-0000-0000-0000-000000000623',2,2,'USD','held')
            """);
        Assert.Equal(2L, await ScalarAsync<long>(database, "SELECT sum(units_covered)::bigint FROM shop.discount_uses WHERE status='held'"));
        await ExecuteAsync(database, "UPDATE shop.discount_uses SET status='released',transitioned_at=CURRENT_TIMESTAMP");
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT max_total_uses FROM shop.discounts"));
        await RejectAsync(database, "UPDATE shop.discount_revisions SET max_total_uses=100");
        await RejectAsync(database, "UPDATE shop.discount_uses SET status='held'");
    }

    [Fact]
    public async Task RetentionKeepsCutoffEqualityAndActiveConsent()
    {
        await using var database = await CreateAsync();
        await ExecuteTransactionAsync(database, """
            SET LOCAL TIME ZONE 'Europe/Skopje';
            INSERT INTO shop.bot_activity_events(id,user_id,event_type,source_kind,action_code,outcome_code,idempotency_key,occurred_at)
            VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000101','navigation','command','start','accepted','public-equality',CURRENT_TIMESTAMP-INTERVAL '4320 hours');
            DO $$ BEGIN
                BEGIN DELETE FROM shop.bot_activity_events WHERE idempotency_key='public-equality';
                    RAISE EXCEPTION 'cutoff equality was deleted';
                EXCEPTION WHEN check_violation THEN NULL; END;
            END $$;
            INSERT INTO shop.bot_activity_events(id,user_id,event_type,source_kind,action_code,outcome_code,idempotency_key,occurred_at)
            VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000101','navigation','command','start','accepted','public-expired',CURRENT_TIMESTAMP-INTERVAL '4320 hours'-INTERVAL '1 second');
            DELETE FROM shop.bot_activity_events WHERE idempotency_key='public-expired'
            """);
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT count(*) FROM shop.bot_activity_events WHERE idempotency_key IN ('public-equality','public-expired')"));
        await ExecuteAsync(database, "INSERT INTO shop.product_stock_subscriptions(id,user_id,product_id,created_at) VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000101','00000000-0000-0000-0000-000000000201',CURRENT_TIMESTAMP-INTERVAL '500 days')");
        await RejectAsync(database, "DELETE FROM shop.product_stock_subscriptions");
        await RejectAsync(database, "INSERT INTO shop.bot_activity_events(id,user_id,event_type,source_kind,action_code,outcome_code,idempotency_key) VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000101','quantity','command','start','accepted','public-invalid-registry-pair')");
    }

    private static string AttemptSql(int number, string operation, string amount, string parent) => $$"""
        INSERT INTO shop.supplier_request_attempts(id,supplier_request_id,attempt_no,operation,max_charge_amount,parent_submit_attempt_id)
        VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000602',{{number}},'{{operation}}',{{amount}},{{parent}})
        """;

    private static async Task SeedSupplierAsync(DisposablePostgres database)
    {
        await ExecuteTransactionAsync(database, """
            INSERT INTO shop.products(id,fulfillment_kind,availability_policy,title) VALUES ('00000000-0000-0000-0000-000000000203','supplier','immediate','public supplier product');
            INSERT INTO shop.brand_products(brand_id,product_id,price_usdt) VALUES ('00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000203',10);
            INSERT INTO shop.product_supplier_sources(id,product_id,provider_code,external_product_id,enabled)
            VALUES ('00000000-0000-0000-0000-000000000601','00000000-0000-0000-0000-000000000203','synthetic','public-external-product',true);
            UPDATE shop.products SET active_supplier_source_id='00000000-0000-0000-0000-000000000601',revision=1 WHERE id='00000000-0000-0000-0000-000000000203';
            """ + OrderSql("00000000-0000-0000-0000-000000000404", UserA, true, "00000000-0000-0000-0000-000000000203", "supplier") + """
            INSERT INTO shop.supplier_requests(id,order_id,requested_quantity,product_supplier_source_id,source_revision,provider_code,account_ref,external_product_id,
                local_intent_key,request_sha256,status,cost_baseline_amount,cost_currency,cost_quote_ref,cost_observed_at)
            VALUES ('00000000-0000-0000-0000-000000000602','00000000-0000-0000-0000-000000000404',2,'00000000-0000-0000-0000-000000000601',1,'synthetic','public-account',
                'public-external-product','public-intent',decode(repeat('11',32),'hex'),'pending',10,'USD','public-quote',CURRENT_TIMESTAMP)
            """);
    }

    [Fact]
    public async Task CompensationPostsOnlyWithItsMatchingTerminalOrderAndCannotReplay()
    {
        await using var database = await CreateAsync();
        await SeedSupplierAsync(database);
        await ExecuteAsync(database, """
            UPDATE shop.supplier_requests SET status='failed';
            INSERT INTO shop.wallets(user_id) VALUES ('00000000-0000-0000-0000-000000000101');
            INSERT INTO shop.audit_events(id,actor_kind,actor_admin_id,brand_id,action,entity_kind,entity_id,reason,correlation_id)
            VALUES ('00000000-0000-0000-0000-000000000903','admin','00000000-0000-0000-0000-000000000301','00000000-0000-0000-0000-000000000001',
                'supplier_credit','supplier_request','00000000-0000-0000-0000-000000000602','public reconciled synthetic resolution',gen_random_uuid())
            """);
        const string credit = """
            UPDATE shop.wallets SET balance=20,version=1 WHERE user_id='00000000-0000-0000-0000-000000000101';
            INSERT INTO shop.wallet_entries(id,user_id,signed_amount,balance_after,posting_version,movement_kind,is_posting,idempotency_key,order_id,supplier_request_id,admin_id,audit_event_id,reason)
            VALUES ('00000000-0000-0000-0000-000000000904','00000000-0000-0000-0000-000000000101',20,20,1,'supplier_wallet_credit',true,'public-reconciled-credit',
                '00000000-0000-0000-0000-000000000404','00000000-0000-0000-0000-000000000602','00000000-0000-0000-0000-000000000301','00000000-0000-0000-0000-000000000903','public reconciled synthetic resolution');
            """;
        const string resolution = """
            UPDATE shop.orders SET fulfillment_status='compensated',resolution_kind='supplier_wallet_credit',resolution_admin_id='00000000-0000-0000-0000-000000000301',
                resolution_audit_event_id='00000000-0000-0000-0000-000000000903',resolution_at=CURRENT_TIMESTAMP,resolution_wallet_entry_id='00000000-0000-0000-0000-000000000904'
            WHERE id='00000000-0000-0000-0000-000000000404';
            """;
        await RejectAsync(database, credit);
        await ExecuteTransactionAsync(database, credit + resolution);
        Assert.Equal(20m, await ScalarAsync<decimal>(database, "SELECT balance FROM shop.wallets"));
        await RejectAsync(database, credit + resolution);
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='supplier_wallet_credit'"));
    }

    private const string UserA = "00000000-0000-0000-0000-000000000101";
    private const string UserB = "00000000-0000-0000-0000-000000000102";
    private const string UserA2 = "00000000-0000-0000-0000-000000000103";

    private static string OrderSql(string id, string user, bool paid = false,
        string product = "00000000-0000-0000-0000-000000000201", string kind = "manual", bool preorder = false, int quantity = 2) => $$"""
        INSERT INTO shop.orders(id,user_id,product_id,quantity,order_kind,payment_status,currency,payment_method,
            unit_price_usdt,auto_price_toman_snapshot,pricing_rounding_step_snapshot,price_calculation_version,
            brand_public_name_snapshot,product_title_snapshot,payment_terms_snapshot,fulfillment_kind_snapshot,local_first_snapshot,is_preorder,payment_due_at,payment_confirmed_at)
        VALUES ('{{id}}','{{user}}','{{product}}',{{quantity}},'product_purchase','{{(paid ? "paid" : "awaiting_payment")}}','USDT','{{(paid ? "wallet" : "crypto")}}',
            10,false,5000,'public-test-v1','public test brand','public test product','public test terms','{{kind}}',true,{{(preorder ? "true" : "false")}},
            {{(paid ? "NULL" : "CURRENT_TIMESTAMP+INTERVAL '30 minutes'")}},{{(paid ? "CURRENT_TIMESTAMP" : "NULL")}});
        """;

    private static string UnitSql(int number) => $$"""
        INSERT INTO shop.fulfillments(id,order_id,unit_no,kind,status,delivered_content,delivered_content_key_id,content_kind)
        VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000403',{{number}},'manual','ready',decode('010203','hex'),'public-synthetic-key','code');
        """;

    private const string ManifestSql = """
        UPDATE shop.orders SET fulfillment_status='ready',delivery_generation='00000000-0000-0000-0000-000000000801',
            manifest_asset_ref='public-synthetic-encrypted-manifest',manifest_digest=decode(repeat('01',32),'hex'),
            manifest_prepared_at=CURRENT_TIMESTAMP,release_authorized_at=CURRENT_TIMESTAMP
        WHERE id='00000000-0000-0000-0000-000000000403';
        """;

    private static async Task<DisposablePostgres> CreateAsync()
    {
        var database = await DisposablePostgres.CreateAsync(Ct);
        try
        {
            await SchemaRunner.ApplyAsync(database.Data, SchemaManifest.ForQualification(database.Data.ConnectionString), Ct);
            await ExecuteTransactionAsync(database, """
                INSERT INTO shop.brands(id,code,internal_name,public_name,market,default_language,bot_username,bot_id,bot_token_ciphertext,bot_token_key_id)
                VALUES ('00000000-0000-0000-0000-000000000001','a','public-a','public a','iran','fa','public_a',1001,decode('01','hex'),'public-key-a'),
                       ('00000000-0000-0000-0000-000000000002','b','public-b','public b','international','en','public_b',1002,decode('02','hex'),'public-key-b');
                INSERT INTO shop.users(id,brand_id,telegram_id,language) VALUES
                    ('00000000-0000-0000-0000-000000000101','00000000-0000-0000-0000-000000000001',7001,'fa'),
                    ('00000000-0000-0000-0000-000000000102','00000000-0000-0000-0000-000000000002',7001,'en'),
                    ('00000000-0000-0000-0000-000000000103','00000000-0000-0000-0000-000000000001',7002,'fa');
                INSERT INTO shop.admins(id,login,authentication_mode,password_verifier)
                    VALUES ('00000000-0000-0000-0000-000000000301','public-admin','password','public-synthetic-verifier');
                INSERT INTO shop.products(id,fulfillment_kind,availability_policy,title) VALUES
                    ('00000000-0000-0000-0000-000000000201','manual','immediate','public synthetic product'),
                    ('00000000-0000-0000-0000-000000000202','local','immediate','public hidden product');
                INSERT INTO shop.brand_products(brand_id,product_id,price_usdt) VALUES
                    ('00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000201',10),
                    ('00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000201',10),
                    ('00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000202',10);
                """ + OrderSql("00000000-0000-0000-0000-000000000401", UserA) +
                OrderSql("00000000-0000-0000-0000-000000000402", UserB) +
                OrderSql("00000000-0000-0000-0000-000000000403", UserA2, true));
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private static async Task ExecuteAsync(DisposablePostgres database, string sql)
    {
        await using var command = database.Data.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task ExecuteTransactionAsync(DisposablePostgres database, string sql)
    {
        await using var connection = await database.Data.OpenConnectionAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(Ct);
        await transaction.CommitAsync(Ct);
    }

    private static async Task RejectAsync(DisposablePostgres database, string sql, string sqlState = PostgresErrorCodes.CheckViolation)
    {
        await using var connection = await database.Data.OpenConnectionAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        var error = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync(Ct);
            await transaction.CommitAsync(Ct);
        });
        Assert.Equal(sqlState, error.SqlState);
    }

    private static async Task<T> ScalarAsync<T>(DisposablePostgres database, string sql)
    {
        await using var command = database.Data.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }
}
