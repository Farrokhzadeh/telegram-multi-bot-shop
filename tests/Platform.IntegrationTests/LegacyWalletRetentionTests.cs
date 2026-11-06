using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Platform.Migration;
using Platform.Persistence;
using Platform.Testing;
namespace Platform.IntegrationTests;

[Collection("PostgreSQL races")]
public sealed class LegacyWalletRetentionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public Task NativeAndManagedWalletRetentionUseTheSameRepeatSafeQualification() => LegacyWalletRetentionQualification.RunAsync(Ct);
    [Fact]
    public async Task MissingChangedOrDiscardedRetentionNeverWritesMoneyOrMappings()
    {
        await using var world = await LegacyWalletRetentionFixture.CreateAsync(Ct); var f = world.Fixture; var row = world.Wallet;
        var store = new OpeningBalanceImportWorkflow(f.Sessions);
        await Assert.ThrowsAsync<MigrationRejectedException>(() => store.ImportAsync(f.Context(0), world.Manifest, row, Ct));
        await Assert.ThrowsAsync<MigrationRejectedException>(() => store.ImportAsync(f.Context(0), world.Manifest, row, Ct, (_, _) => throw new FileNotFoundException("PRIVATE")));
        await Assert.ThrowsAsync<MigrationRejectedException>(() => store.ImportAsync(f.Context(0), world.Manifest, row, Ct, (_, _) => Task.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes("PRIVATE changed register"))));
        await Assert.ThrowsAsync<MigrationRejectedException>(() => store.ImportAsync(f.Context(0), world.Manifest, row with { OtherBalance = 0, Retention = null }, Ct));
        Assert.Throws<MigrationRejectedException>(() => (row with { OtherBalance = 1234.51m }).Validate());
        Assert.Throws<MigrationRejectedException>(() => (row with { LegacyUser = "99009999" }).Validate());
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.users", Ct)); Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", Ct)); Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.legacy_id_map", Ct));
    }
    [Theory]
    [InlineData("UPDATE public.transactions SET user_id=99009999 WHERE id='irr-original'")]
    [InlineData("UPDATE public.transactions SET currency='USD' WHERE id='irr-original'")]
    [InlineData("UPDATE public.transactions SET amount=-900.26 WHERE id='irr-original'")]
    [InlineData("UPDATE public.transactions SET created_at=created_at+interval '1 second' WHERE id='irr-original'")]
    [InlineData("DELETE FROM public.transactions WHERE id='zero-original'")]
    [InlineData("UPDATE public.users SET balance_irr=1234.51")]
    public async Task OriginalNativeOwnershipCurrencyAmountClockAndZeroCoverageAreRechecked(string mutation)
    {
        await using var world = await LegacyWalletRetentionFixture.CreateAsync(Ct); var f = world.Fixture; var row = world.Wallet;
        await using (var change = f.Database.Data.CreateCommand(mutation)) await change.ExecuteNonQueryAsync(Ct);
        await Assert.ThrowsAsync<MigrationRejectedException>(() => new ImportWorkflow(world.Source, f.Sessions).ImportWalletAsync(f.Context(0), world.Manifest, row.TelegramId, row.TargetUser, row.TargetOpening, row.History.ToDictionary(h => h.LegacyId, h => h.TargetEntry), Ct, retention: row.Retention, readRetention: world.Read));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", Ct)); Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.legacy_id_map", Ct));
    }
    [Fact]
    public async Task DeferredWalletFailureRollsBackTheWholeRetainedProjectionAndRetryCommitsOnce()
    {
        await using var world = await LegacyWalletRetentionFixture.CreateAsync(Ct); var f = world.Fixture; var row = world.Wallet;
        await using (var fault = f.Database.Data.CreateCommand("CREATE FUNCTION shop.reject_retained_wallet() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'Public retained wallet commit failure' USING ERRCODE='23514'; END $$; CREATE CONSTRAINT TRIGGER reject_retained_wallet AFTER INSERT ON shop.wallet_entries DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION shop.reject_retained_wallet()")) await fault.ExecuteNonQueryAsync(Ct);
        var store = new OpeningBalanceImportWorkflow(f.Sessions);
        await Assert.ThrowsAsync<DbOperationException>(() => store.ImportAsync(f.Context(0), world.Manifest, row, Ct, world.Read));
        foreach (string table in new[] { "users", "wallets", "wallet_entries", "legacy_id_map", "audit_events" }) Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop." + table, Ct));
        await using (var repair = f.Database.Data.CreateCommand("DROP TRIGGER reject_retained_wallet ON shop.wallet_entries")) await repair.ExecuteNonQueryAsync(Ct);
        await store.ImportAsync(f.Context(0), world.Manifest, row, Ct, world.Read); await store.ImportAsync(f.Context(0), world.Manifest, row, Ct, world.Read);
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", Ct));
        var report = await new ReconciliationReport(f.Sessions).CompareWalletsAsync(world.Manifest, [row], Ct, world.Read);
        Assert.True(report.Matched); Assert.Equal(1L, report.ManualIrrAccounts);
        string json = JsonSerializer.Serialize(report, MigrationReportJsonContext.Default.WalletRecordReconciliation);
        foreach (string privateValue in new[] { "99001234", "1234.50", "900.25", "7.50", "PRIVATE", "manual-wallet-history", "usd-original", "irr-original" }) Assert.DoesNotContain(privateValue, json, StringComparison.Ordinal);
    }
    [Theory]
    [InlineData("brand", "acc-abad", "zikotech")]
    [InlineData("source", "prod1", "prod2")]
    [InlineData("currency", "IRR", "EUR")]
    [InlineData("original_irr_amount", "1234.50", "NaN")]
    public void InvalidRegisterFactsRejectEvenWithANewFileDigest(string field, string original, string changed)
    {
        byte[] body = LegacyWalletRetentionFixture.RegisterBody();
        string text = Encoding.UTF8.GetString(body).Replace("\"" + field + "\":\"" + original + "\"", "\"" + field + "\":\"" + changed + "\"", StringComparison.Ordinal); byte[] invalid = Encoding.UTF8.GetBytes(text);
        Assert.Throws<MigrationRejectedException>(() => LegacyWalletRetention.Open("protected:synthetic/manual-wallet-history", SHA256.HashData(invalid), invalid, "acc-abad", LegacyWalletRetentionFixture.Archive, "99001234"));
    }
    [Fact]
    public async Task NativeHistoryCrossesPageBoundariesWithoutPostingRetainedZeroOrIrrRows()
    {
        await using var world = await LegacyWalletRetentionFixture.CreateAsync(Ct); var f = world.Fixture;
        await using (var add = f.Database.Data.CreateCommand("INSERT INTO public.transactions SELECT 'bulk-'||lpad(n::text,4,'0'),99001234,0,'IRR','2024-01-01 02:00Z'::timestamptz FROM generate_series(1,1003) n")) await add.ExecuteNonQueryAsync(Ct);
        var json = JsonNode.Parse(world.Register)!; var history = json["sources"]![0]!["history"]!.AsArray();
        for (int i = 1; i <= 1003; i++) history.Add(new JsonObject { ["id"] = "bulk-" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture), ["user_id"] = "99001234", ["amount"] = "0", ["currency"] = "IRR", ["created_at"] = "2024-01-01T02:00:00Z" });
        byte[] bytes = Encoding.UTF8.GetBytes(json.ToJsonString());
        var retention = LegacyWalletRetention.Open("protected:synthetic/paginated-history", SHA256.HashData(bytes), bytes, "acc-abad", LegacyWalletRetentionFixture.Archive, "99001234"); var row = world.Wallet with { Retention = retention };
        byte[] plan = VerifiedImportManifest.EncodePlan(SchemaManifest.ForQualification(f.Database.Data.ConnectionString), "acc-abad", f.Brands[0], world.Manifest.SourceRevision, Convert.FromHexString(world.Manifest.SourceSchemaDigest), world.Manifest.SnapshotReference, Convert.FromHexString(LegacyWalletRetentionFixture.Archive), Guid.NewGuid(), row.Proofs(f.Brands[0]), row.BalanceUsd);
        var manifest = VerifiedImportManifest.Open("protected:synthetic/paginated-wallet-plan", SHA256.HashData(plan), plan);
        Task<ReadOnlyMemory<byte>> Read(string reference, CancellationToken ct) => reference == retention.Reference ? Task.FromResult<ReadOnlyMemory<byte>>(bytes) : throw new KeyNotFoundException();
        var workflow = new ImportWorkflow(world.Source, f.Sessions);
        for (int pass = 0; pass < 2; pass++) Assert.True((await workflow.ImportWalletAsync(f.Context(0), manifest, row.TelegramId, row.TargetUser, row.TargetOpening, row.History.ToDictionary(h => h.LegacyId, h => h.TargetEntry), Ct, retention: retention, readRetention: Read)).Complete);
        var report = await new ReconciliationReport(f.Sessions).CompareWalletsAsync(manifest, [row], Ct, Read);
        Assert.True(report.Matched); Assert.Equal(1006L, report.RetainedHistoryRows); Assert.Equal(1004L, report.RetainedIrrHistory); Assert.Equal(1004L, report.RetainedZeroHistory); Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", Ct));
    }

}
