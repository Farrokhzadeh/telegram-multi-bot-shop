using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using Platform.Migration;
using Platform.Persistence;
using Platform.Testing;
namespace Platform.IntegrationTests;

[Collection("PostgreSQL races")]
public sealed class LegacyRecordImportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static VerifiedImportManifest Manifest(CheckoutFixture f, Guid batch, IEnumerable<LegacyMapProof> proofs, decimal opening = 0, byte[]? schema = null) => LegacyRecordQualification.Manifest(f, batch, proofs, opening, schema);
    [Theory]
    [InlineData(LegacyProjection.OrderRecord, "orders")]
    [InlineData(LegacyProjection.ProductRecord, "products")]
    [InlineData(LegacyProjection.DiscountRecord, "discounts")]
    [InlineData(LegacyProjection.PaymentAttempt, "payment_attempts")]
    [InlineData(LegacyProjection.ManualDeposit, "manual_deposits")]
    [InlineData(LegacyProjection.InventoryUnit, "inventory")]
    [InlineData(LegacyProjection.Delivery, "common_orderdelivery")]
    [InlineData(LegacyProjection.DeliveryPart, "common_orderdeliverypart")]
    [InlineData(LegacyProjection.PreorderBatch, "common_preorderdeliverybatch")]
    [InlineData(LegacyProjection.PreorderPart, "common_preorderdeliverybatchpart")]
    [InlineData(LegacyProjection.PreorderItem, "common_preorderdeliverybatch_items")]
    [InlineData(LegacyProjection.SupplierFulfillment, "supplier_fulfillments")]
    [InlineData(LegacyProjection.SupplierItem, "supplier_fulfillment_items")]
    [InlineData(LegacyProjection.SupplierAttempt, "supplier_procurement_attempts")]
    [InlineData(LegacyProjection.SupplierRefundArchive, "supplier_refund_records")]
    [InlineData(LegacyProjection.PreorderState, "preorder_order_states")]
    [InlineData(LegacyProjection.PreorderRefundArchive, "preorder_refund_records")]
    [InlineData(LegacyProjection.ServiceJob, "service_jobs")]
    [InlineData(LegacyProjection.ZeroTransaction, "transactions")]
    public async Task WholeRecordReaderBindsEveryFieldAndRejectsOmissionsAndSchemaDrift(LegacyProjection projection, string table)
    {
        await using var f = await CheckoutFixture.CreateAsync(users: 0, stock: 0, ct: Ct);
        string key = projection == LegacyProjection.DiscountRecord ? "code" : "id";
        await using (var setup = f.Database.Data.CreateCommand("CREATE TABLE public.django_migrations(id bigint PRIMARY KEY,app text,name text); INSERT INTO public.django_migrations VALUES(1,'store','0001_initial'); CREATE TABLE public." + table + "(" + key + " text PRIMARY KEY,order_id text,amount numeric(24,2),original_payload text,active boolean DEFAULT true,is_removed boolean DEFAULT false); INSERT INTO public." + table + "(" + key + ",order_id,amount,original_payload) VALUES('one','ORD-ABC123ABC123',0,'private synthetic provider evidence')")) await setup.ExecuteNonQueryAsync(Ct);
        var endpoint = new NpgsqlConnectionStringBuilder(f.Database.Data.ConnectionString);
        var reader = new LegacySourceReader(f.Database.Data, new(endpoint.Host!, endpoint.Port, endpoint.Database!, endpoint.Username!));
        byte[] schema = await reader.InspectSchemaAsync(Ct); Guid target = Guid.NewGuid(); string body;
        await using (var query = f.Database.Data.CreateCommand("SELECT to_jsonb(s)::text FROM public." + table + " s")) body = (string)(await query.ExecuteScalarAsync(Ct))!;
        var source = new LegacySourceRecord(projection, "one", body);
        var manifest = Manifest(f, Guid.NewGuid(), [source.Proof(target, f.Context(0), f.Secrets)], schema: schema);
        Assert.Equal(source, await reader.ReadRecordAsync(f.Context(0), f.Secrets, manifest, projection, "one", target, Ct));
        Assert.Equal(1, await reader.VerifyRecordCoverageAsync(f.Context(0), f.Secrets, manifest, projection, Ct));
        await using (var extra = f.Database.Data.CreateCommand("INSERT INTO public." + table + "(" + key + ",order_id,amount,original_payload) VALUES('two','ORD-ABC123ABC123',0,'extra')")) await extra.ExecuteNonQueryAsync(Ct);
        await Assert.ThrowsAsync<MigrationRejectedException>(() => reader.VerifyRecordCoverageAsync(f.Context(0), f.Secrets, manifest, projection, Ct));
        await using (var change = f.Database.Data.CreateCommand("UPDATE public." + table + " SET original_payload='changed' WHERE " + key + "='one'")) await change.ExecuteNonQueryAsync(Ct);
        await Assert.ThrowsAsync<MigrationRejectedException>(() => reader.ReadRecordAsync(f.Context(0), f.Secrets, manifest, projection, "one", target, Ct));
        await using (var change = f.Database.Data.CreateCommand("ALTER TABLE public." + table + " ADD COLUMN unexpected text")) await change.ExecuteNonQueryAsync(Ct);
        await Assert.ThrowsAsync<MigrationRejectedException>(() => reader.ReadRecordAsync(f.Context(0), f.Secrets, manifest, projection, "one", target, Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.legacy_id_map", Ct));
    }
    [Theory]
    [InlineData("wallet")]
    [InlineData("partial")]
    [InlineData("free")]
    [InlineData("bank")]
    [InlineData("crypto")]
    [InlineData("topup")]
    [InlineData("preorder")]
    [InlineData("api")]
    [InlineData("service")]
    public async Task FrozenRecordsImportOnceWithoutNewMoneyStockOrCustomerSendsAndReconcileExactly(string route)
    {
        await using var world = await LegacyRecordFixture.CreateAsync(route, Ct);
        var f = world.Fixture; var manifest = world.Manifest; var set = world.Set; var reports = world.Reports; var protectedFiles = world.ProtectedFiles;
        var historical = set.Orders[0]; var wallet = set.Wallets[0]; const string reference = "ORD-ABC123ABC123";
        var catalog = new LegacyCatalogImportWorkflow(f.Sessions, f.Secrets, f.Brands[0]); var orders = new LegacyOrderRecordImportWorkflow(f.Sessions, f.Secrets);
        Task<ProtectedLegacyEvidence> Read(string asset, CancellationToken ct) => world.Read(asset, ct);
        var report = await reports.CompareAsync(f.Context(0), manifest, set, Read, Ct); Assert.True(report.Matched); Assert.False(report.ReleaseReady);
        string json = JsonSerializer.Serialize(report, MigrationReportJsonContext.Default.LegacyRecordReconciliation);
        foreach (string value in new[] { reference, "PRIVATE-COUPON", "PRIVATE-LEGACY-CODE", "Original private brand", "private original receipt", "123456789" }) Assert.DoesNotContain(value, json, StringComparison.Ordinal);
        Assert.Equal(12.34m, (await new ReconciliationReport(f.Sessions).ReadAsync(manifest, Ct)).OpeningUsd);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE is_posting", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.inventory_items", Ct)); Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.jobs", Ct)); Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discounts WHERE max_total_uses=10 AND redeemed_count=0 AND usage_adjustment_units=3 AND created_by_admin_id IS NULL", Ct));
        Assert.False((await reports.CompareAsync(f.Context(0), manifest, set with { Delivery = [] }, Read, Ct)).Matched);
        var original = protectedFiles[historical.Record.Reference]; byte[] damaged = original.Ciphertext.ToArray(); damaged[^1] ^= 1; protectedFiles[historical.Record.Reference] = original with { Ciphertext = damaged };
        Assert.Equal(1L, (await reports.CompareAsync(f.Context(0), manifest, set, Read, Ct)).InvalidArchives); protectedFiles[historical.Record.Reference] = original;
        await Assert.ThrowsAsync<MigrationRejectedException>(() => catalog.ImportAsync(f.Context(1), manifest, set.Catalog[0], Ct));
        await using (var change = f.Database.Data.CreateCommand("UPDATE shop.orders SET product_title_snapshot='changed'")) await Assert.ThrowsAsync<PostgresException>(() => change.ExecuteNonQueryAsync(Ct));
        await using (var change = f.Database.Data.CreateCommand("UPDATE shop.orders SET fulfillment_status='waiting_stock'")) await change.ExecuteNonQueryAsync(Ct);
        Assert.False((await reports.CompareAsync(f.Context(0), manifest, set, Read, Ct)).Matched);
        await Assert.ThrowsAsync<MigrationRejectedException>(() => orders.ImportAsync(f.Context(0), manifest, historical, Ct));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportedMemberRestrictionsAndNamesAreCommittedAndCannotBeDroppedOnReplay(bool originalBanTime)
    {
        await using var f = await CheckoutFixture.CreateAsync(users: 0, stock: 0, ct: Ct); await MigrationQualification.EnableRoleAsync(f, Ct);
        var source = MigrationQualification.Wallet() with { LegacyUser = "99001234", State = new("synthetic", "Original", "Member", false, true, new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc)) };
        source = source with { State = source.State! with { BannedAt = originalBanTime ? new DateTime(2024, 1, 20, 0, 0, 0, DateTimeKind.Utc) : null }, History = source.History.Select(h => h with { BalanceAfter = null }).ToArray() };
        await using (var setup = f.Database.Data.CreateCommand("""
            CREATE TABLE public.django_migrations(id bigint PRIMARY KEY,app text,name text); INSERT INTO public.django_migrations VALUES(1,'accounts','0001_initial');
            CREATE TABLE public.users(id bigint PRIMARY KEY,balance numeric(18,2),balance_irr numeric(24,2),lang text,joined_at timestamptz,last_seen_at timestamptz,is_active boolean,is_banned boolean,username text,first_name text,last_name text);
            INSERT INTO public.users VALUES(99001234,12.34,0,'en','2024-01-01 00:00Z','2024-01-02 00:00Z',false,true,'synthetic','Original','Member');
            CREATE TABLE public.transactions(id text PRIMARY KEY,user_id bigint,amount numeric(24,2),currency text,created_at timestamptz);
            INSERT INTO public.transactions VALUES('source-entry-1',99001234,100,'USD','2024-01-01 01:00Z'),('source-entry-2',99001234,-10,'USD','2024-01-01 02:00Z');
            """)) await setup.ExecuteNonQueryAsync(Ct);
        if (originalBanTime)
        {
            await using var setup = f.Database.Data.CreateCommand("ALTER TABLE public.users ADD COLUMN banned_at timestamptz; UPDATE public.users SET banned_at='2024-01-20 00:00Z'"); await setup.ExecuteNonQueryAsync(Ct);
        }
        var endpoint = new NpgsqlConnectionStringBuilder(f.Database.Data.ConnectionString);
        var reader = new LegacySourceReader(f.Database.Data, new(endpoint.Host!, endpoint.Port, endpoint.Database!, endpoint.Username!));
        var manifest = Manifest(f, Guid.NewGuid(), source.Proofs(f.Brands[0]), source.BalanceUsd, await reader.InspectSchemaAsync(Ct)); var store = new OpeningBalanceImportWorkflow(f.Sessions);
        var targets = source.History.ToDictionary(h => h.LegacyId, h => h.TargetEntry);
        await Assert.ThrowsAsync<MigrationRejectedException>(() => reader.ReadWalletAsync(manifest, source.TelegramId, source.TargetUser, source.TargetOpening, targets, Ct));
        var actual = await reader.ReadWalletAsync(manifest, source.TelegramId, source.TargetUser, source.TargetOpening, targets, Ct, source.State!.ObservedAt);
        Assert.Equal(source.State, actual.State);
        Assert.Throws<MigrationRejectedException>(() => (source with { State = source.State! with { BannedAt = source.State!.ObservedAt.AddDays(1) } }).Validate()); Assert.Equal(1L, await reader.VerifyWalletCoverageAsync(manifest, Ct));
        await store.ImportAsync(f.Context(0), manifest, source, Ct); await store.ImportAsync(f.Context(0), manifest, source, Ct);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.users WHERE username='synthetic' AND first_name='Original' AND last_name='Member' AND blocked_at IS NOT NULL AND banned_at IS NOT NULL", Ct));
        await using (var check = f.Database.Data.CreateCommand("SELECT banned_at FROM shop.users")) Assert.Equal(source.State!.BannedAt ?? source.State!.ObservedAt, (DateTime)(await check.ExecuteScalarAsync(Ct))!);
        await Assert.ThrowsAsync<MigrationRejectedException>(() => store.ImportAsync(f.Context(0), manifest, source with { State = source.State! with { BannedAt = new DateTime(2024, 1, 21, 0, 0, 0, DateTimeKind.Utc) } }, Ct));
        await Assert.ThrowsAsync<MigrationRejectedException>(() => store.ImportAsync(f.Context(0), manifest, source with { State = null }, Ct));
    }
    [Fact]
    public async Task PartialLegacyPricesCannotBeRoundedIntoDifferentTargetMoney()
    {
        await using var f = await CheckoutFixture.CreateAsync(users: 0, stock: 0, ct: Ct);
        const string id = "ORD-FED123FED123"; Guid target = Guid.NewGuid();
        var source = new LegacySourceRecord(LegacyProjection.OrderRecord, id, """{"id":"ORD-FED123FED123","user_id":99001234,"product_id":456,"qty":3,"total":"100","currency":"USD","created_at":"2024-02-01T00:00:00Z","checkout_snapshot":{"version":1,"product_id":456,"product_name":"Original synthetic partial price","product_type":"credential","quantity":3,"original_usd":"300","total_usd":"100","original_toman":"9000000","total_toman":"3000000","savings_usd":"200","savings_toman":"6000000","discount_code":"PRIVATE-PARTIAL","discount_pct":"100","discount_quantity":2,"currency":"USD","payment_method":"wallet"}}""");
        var manifest = Manifest(f, Guid.NewGuid(), [source.Proof(target, f.Context(0), f.Secrets)]);
        var archived = await new LegacyRecordArchive(f.Secrets).WriteAsync(f.Context(0), manifest, source, target, (_, _) => Task.FromResult("protected:synthetic/unrepresentable-price"), Ct);
        Assert.Throws<MigrationRejectedException>(() => new LegacyHistoricalRecord(archived, Guid.NewGuid(), "Original brand").Proofs(f.Context(0), f.Secrets));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders", Ct));
    }
    [Fact]
    public async Task ImportExceptionsCannotCreateOrdinaryBankPaymentsOrAssignUnownedPaidCodes()
    {
        await using var world = await LegacyRecordFixture.CreateAsync("bank", Ct); var f = world.Fixture;
        await using (var session = await f.Sessions.BeginAsync(Ct))
            await Assert.ThrowsAsync<DbOperationException>(() => session.ExecuteAsync(new DbStatement("INSERT INTO shop.payments(id,order_id,payment_method,quote_currency,amount_due,idempotency_ref) VALUES($1,$2,'bank','TOMAN',1,'missing-destination')", DbValue.Uuid(Guid.NewGuid()), DbValue.Uuid(world.Set.Orders[0].Record.Target)), Ct));
        await using (var session = await f.Sessions.BeginAsync(Ct))
            await Assert.ThrowsAsync<DbOperationException>(() => session.ExecuteAsync(new DbStatement("INSERT INTO shop.payments(id,order_id,payment_method,quote_currency,amount_due,idempotency_ref,legacy_kind) VALUES($1,$2,'bank','TOMAN',1,'missing-proof','manual_deposit')", DbValue.Uuid(Guid.NewGuid()), DbValue.Uuid(world.Set.Orders[0].Record.Target)), Ct));
        var source = new LegacySourceRecord(LegacyProjection.DiscountRecord, "UNOWNED", """{"code":"UNOWNED","product_id":456,"assigned_user_id":null,"pct_off":"100","max_uses":10,"current_uses":1,"active":true}""");
        Guid target = Guid.NewGuid(); var manifest = Manifest(f, Guid.NewGuid(), [source.Proof(target, f.Context(0), f.Secrets)]);
        var archive = await new LegacyRecordArchive(f.Secrets).WriteAsync(f.Context(0), manifest, source, target, (_, _) => Task.FromResult("protected:synthetic/quarantined-unowned"), Ct);
        var coupon = new LegacyCouponRecord(archive, Guid.NewGuid(), world.Set.Catalog[0].Target, null, "prepaid_entitlement", world.Set.Orders[0].Record.Target);
        Assert.Throws<MigrationRejectedException>(() => coupon.Proofs(f.Context(0), f.Secrets));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discounts", Ct)); Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE is_posting", Ct));
    }
    [Fact]
    public async Task SameLegacyPaymentIdAcrossBrandsRetainsSeparateRepeatSafeOrigins()
    {
        await using var f = await CheckoutFixture.CreateAsync(users: 0, stock: 0, ct: Ct); await MigrationQualification.EnableRoleAsync(f, Ct);
        for (int brand = 0; brand < 2; brand++)
        {
            var context = f.Context(brand); Guid target = Guid.NewGuid();
            var wallet = MigrationQualification.Wallet() with { TargetUser = Guid.NewGuid(), TargetOpening = Guid.NewGuid(), BalanceUsd = 0, History = [] };
            var source = new LegacySourceRecord(LegacyProjection.PaymentAttempt, "shared-payment", """{"id":"shared-payment","user_id":99001234,"kind":"wallet_topup","expected_amount":"5","expected_currency":"USD","pay_currency":"usdtbsc","fulfilled_order_id":null,"status":"created","created_at":"2024-02-01T00:00:00Z"}""");
            wallet = wallet with { TelegramId = 99001234, LegacyUser = "99001234" };
            byte[] body = VerifiedImportManifest.EncodePlan(SchemaManifest.ForQualification(f.Database.Data.ConnectionString), "synthetic-" + brand, context.BrandId, "66371a23a1c8149dc5bdf89a0355aa689d454dba", SHA256.HashData("synthetic schema"u8), "protected:synthetic/collision-copy", SHA256.HashData("synthetic copy"u8), Guid.NewGuid(), wallet.Proofs(context.BrandId).Append(source.Proof(target, context, f.Secrets)));
            var manifest = VerifiedImportManifest.Open("protected:synthetic/collision-plan", SHA256.HashData(body), body);
            var archived = await new LegacyRecordArchive(f.Secrets).WriteAsync(context, manifest, source, target, (_, _) => Task.FromResult("protected:synthetic/payment-copy"), Ct);
            await new OpeningBalanceImportWorkflow(f.Sessions).ImportAsync(context, manifest, wallet, Ct);
            var payment = new LegacyPaymentRecord(archived, wallet.TargetUser, null); var store = new LegacyPaymentImportWorkflow(f.Sessions, f.Secrets);
            await store.ImportAsync(context, manifest, payment, Ct); await store.ImportAsync(context, manifest, payment, Ct);
        }
        Assert.Equal(2L, await f.CountAsync("SELECT count(DISTINCT idempotency_ref) FROM shop.payments", Ct));
        Assert.Equal(2L, await f.CountAsync("SELECT count(DISTINCT source_brand) FROM shop.legacy_id_map WHERE source_table='payment_attempts' AND legacy_id='shared-payment'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", Ct));
    }
}
