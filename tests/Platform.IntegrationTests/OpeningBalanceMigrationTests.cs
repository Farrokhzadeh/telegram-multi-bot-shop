using Platform.Migration;
using Platform.Testing;

namespace Platform.IntegrationTests;

[Collection("PostgreSQL races")]
public sealed class OpeningBalanceMigrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task RecordComparisonRejectsMissingEvidenceAndTargetDriftWithoutExposingSource()
    {
        await using var f = await CheckoutFixture.CreateAsync(users: 0, stock: 0, ct: Ct);
        await MigrationQualification.EnableRoleAsync(f, Ct);
        var row = MigrationQualification.Wallet();
        var manifest = MigrationQualification.Manifest(f, "public-a", f.Brands[0], [row]);
        var importer = new OpeningBalanceImportWorkflow(f.Sessions);
        var reports = new ReconciliationReport(f.Sessions);
        await importer.ImportAsync(f.Context(0), manifest, row with { History = row.History.Take(1).ToArray() }, Ct);
        Assert.False((await reports.CompareWalletsAsync(manifest, [row], Ct)).Matched);
        Assert.False((await reports.CompareWalletsAsync(manifest, [row with { History = row.History.Take(1).ToArray() }], Ct)).Matched);
        await importer.ImportAsync(f.Context(0), manifest, row, Ct);
        var result = await reports.CompareWalletsAsync(manifest, [row], Ct);
        Assert.True(result.Matched); Assert.False(result.ReleaseReady);
        Assert.False((await reports.CompareWalletsAsync(manifest, [], Ct)).Matched);
        await Assert.ThrowsAsync<MigrationRejectedException>(() => reports.CompareWalletsAsync(manifest, [row with { BalanceUsd = row.BalanceUsd + 1 }], Ct));
        string json = System.Text.Json.JsonSerializer.Serialize(result, MigrationReportJsonContext.Default.WalletRecordReconciliation);
        Assert.DoesNotContain(row.TelegramId.ToString(), json, StringComparison.Ordinal);
        Assert.DoesNotContain(row.LegacyUser, json, StringComparison.Ordinal);
        await using (var change = f.Database.Data.CreateCommand("UPDATE shop.users SET language=CASE WHEN language='en' THEN 'fa' ELSE 'en' END")) await change.ExecuteNonQueryAsync(Ct);
        Assert.True((await reports.ReadAsync(manifest, Ct)).Complete);
        Assert.False((await reports.CompareWalletsAsync(manifest, [row], Ct)).Matched);
        await Assert.ThrowsAsync<MigrationRejectedException>(() => importer.ImportAsync(f.Context(0), manifest, row, Ct));
    }
    [Theory]
    [InlineData("0")]
    [InlineData("12.34")]
    public async Task SnapshotPostsOnceAndIncompleteHistoryNeverPostsOrChangesFrozenOpening(string balance)
    {
        await using var f = await CheckoutFixture.CreateAsync(users: 0, stock: 0, ct: Ct);
        await MigrationQualification.EnableRoleAsync(f, Ct);
        var row = MigrationQualification.Wallet() with { BalanceUsd = Platform.Core.ExactDecimal.Parse(balance) };
        var manifest = MigrationQualification.Manifest(f, "public-a", f.Brands[0], [row]);
        var workflow = new OpeningBalanceImportWorkflow(f.Sessions);
        await workflow.ImportAsync(f.Context(0), manifest, row, Ct);
        await workflow.ImportAsync(f.Context(0), manifest, row, Ct);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallets WHERE balance=" + balance, Ct));
        Assert.Equal(balance == "0" ? 0L : 1L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='opening_balance' AND is_posting", Ct));
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='legacy_history' AND NOT is_posting AND posting_version IS NULL", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.jobs", Ct));
        var report = await new ReconciliationReport(f.Sessions).ReadAsync(manifest, Ct);
        Assert.True(report.Complete);
        Assert.Equal(row.BalanceUsd, report.OpeningUsd);
        Assert.DoesNotContain(row.TelegramId.ToString(), report.ToString());
    }
    [Theory]
    [InlineData("TOMAN", "0")]
    [InlineData("USD", "1")]
    public async Task NonUsdWalletOrOtherLiabilityStopsWithoutCurrencyCoercion(string currency, string otherBalance)
    {
        await using var f = await CheckoutFixture.CreateAsync(users: 0, stock: 0, ct: Ct);
        var valid = MigrationQualification.Wallet();
        var manifest = MigrationQualification.Manifest(f, "public-a", f.Brands[0], [valid]);
        var row = valid with { Currency = currency, OtherBalance = Platform.Core.ExactDecimal.Parse(otherBalance) };
        await Assert.ThrowsAsync<MigrationRejectedException>(() => new OpeningBalanceImportWorkflow(f.Sessions).ImportAsync(f.Context(0), manifest, row, Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallets", Ct));
    }
    [Fact]
    public async Task ResumeFillsMissingNonPostingHistoryWithoutResettingAnAlreadyFrozenOpening()
    {
        await using var f = await CheckoutFixture.CreateAsync(users: 0, stock: 0, ct: Ct);
        await MigrationQualification.EnableRoleAsync(f, Ct);
        var row = MigrationQualification.Wallet();
        var manifest = MigrationQualification.Manifest(f, "public-a", f.Brands[0], [row]);
        var writer = new OpeningBalanceImportWorkflow(f.Sessions);
        await writer.ImportAsync(f.Context(0), manifest, row with { History = row.History.Take(1).ToArray() }, Ct);
        var incomplete = await new ReconciliationReport(f.Sessions).ReadAsync(manifest, Ct);
        Assert.False(incomplete.Complete);
        Assert.Equal(1, incomplete.MissingMappings);
        await writer.ImportAsync(f.Context(0), manifest, row, Ct);
        Assert.True((await new ReconciliationReport(f.Sessions).ReadAsync(manifest, Ct)).Complete);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE is_posting", Ct));
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE NOT is_posting", Ct));
    }
    [Fact]
    public async Task TargetAndMappingRollbackTogetherAndUnprivilegedRoleCannotImport()
    {
        await using var f = await CheckoutFixture.CreateAsync(users: 0, stock: 0, ct: Ct);
        await MigrationQualification.EnableRoleAsync(f, Ct);
        var row = MigrationQualification.Wallet();
        var manifest = MigrationQualification.Manifest(f, "public-a", f.Brands[0], [row]);
        await using (var denied = await f.Sessions.BeginAsync(Ct))
        {
            await denied.ExecuteAsync(new Platform.Persistence.DbStatement("SELECT set_config('role',$1,true)", Platform.Persistence.DbValue.Text("pg_read_all_data")), Ct);
            await Assert.ThrowsAsync<MigrationRejectedException>(() => LegacyMapStore.EnsureAsync(denied, manifest, manifest.Proofs[0], Ct));
        }
        await using (var rollback = await f.Sessions.BeginAsync(Ct))
        {
            await LegacyMapStore.EnsureAsync(rollback, manifest, manifest.Proofs.Single(p => p.Projection == LegacyProjection.User), Ct);
            await rollback.ExecuteAsync(new Platform.Persistence.DbStatement("INSERT INTO shop.users(id,brand_id,telegram_id,language) VALUES ($1,$2,$3,'en')", Platform.Persistence.DbValue.Uuid(row.TargetUser), Platform.Persistence.DbValue.Uuid(f.Brands[0]), Platform.Persistence.DbValue.Bigint(row.TelegramId)), Ct);
        }
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.legacy_id_map", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.users", Ct));
        var bad = row with { TargetUser = Guid.NewGuid() };
        var collision = MigrationQualification.Manifest(f, "public-a", f.Brands[0], [bad]);
        await new OpeningBalanceImportWorkflow(f.Sessions).ImportAsync(f.Context(0), manifest, row, Ct);
        await Assert.ThrowsAsync<MigrationRejectedException>(() => new OpeningBalanceImportWorkflow(f.Sessions).ImportAsync(f.Context(0), collision, bad, Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.users", Ct));
    }
    [Fact]
    public async Task SameLegacyIdAcrossBrandsIsDistinctAndChangedSnapshotCannotReopenTheBalance()
    {
        await using var f = await CheckoutFixture.CreateAsync(users: 0, stock: 0, ct: Ct);
        await MigrationQualification.EnableRoleAsync(f, Ct);
        var a = MigrationQualification.Wallet();
        var b = MigrationQualification.Wallet();
        var ma = MigrationQualification.Manifest(f, "public-a", f.Brands[0], [a]);
        var mb = MigrationQualification.Manifest(f, "public-b", f.Brands[1], [b]);
        var writer = new OpeningBalanceImportWorkflow(f.Sessions);
        await writer.ImportAsync(f.Context(0), ma, a, Ct);
        await writer.ImportAsync(f.Context(1), mb, b, Ct);
        Assert.NotEqual(a.TargetUser, b.TargetUser);
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.users WHERE telegram_id=" + a.TelegramId, Ct));
        var changed = a with { BalanceUsd = 99 };
        await Assert.ThrowsAsync<MigrationRejectedException>(() => writer.ImportAsync(f.Context(0), MigrationQualification.Manifest(f, "public-a", f.Brands[0], [changed]), changed, Ct));
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.wallets WHERE balance=12.34", Ct));
    }
}
