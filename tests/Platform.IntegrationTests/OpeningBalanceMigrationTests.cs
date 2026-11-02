using Platform.Migration;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class OpeningBalanceMigrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
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
        var row = MigrationQualification.Wallet() with { Currency = currency, OtherBalance = Platform.Core.ExactDecimal.Parse(otherBalance) };
        var manifest = MigrationQualification.Manifest(f, "public-a", f.Brands[0], [row]);
        await Assert.ThrowsAsync<MigrationRejectedException>(() => new OpeningBalanceImportWorkflow(f.Sessions).ImportAsync(f.Context(0), manifest, row, Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallets", Ct));
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
