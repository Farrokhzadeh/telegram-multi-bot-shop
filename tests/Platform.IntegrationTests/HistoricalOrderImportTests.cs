using System.Security.Cryptography;
using Platform.Migration;
using Platform.Persistence;
using Platform.Testing;
namespace Platform.IntegrationTests;

[Collection("PostgreSQL races")]
public sealed class HistoricalOrderImportTests
{
    private static LegacyHistoricalOrder Order(LegacyWalletSnapshot wallet) => new(new("ORD-ABC123ABC123", wallet.TelegramId, 456, 2, 12.50m, "USD", wallet.FirstSeen,
        """{"version":1,"product_id":456,"product_name":"Original synthetic title","product_type":"credential","quantity":2,"original_usd":"12.50","total_usd":"12.50","original_toman":"500000","total_toman":"500000","savings_usd":"0.00","savings_toman":"0","currency":"USD","payment_method":"wallet","discount_code":null,"discount_pct":null,"discount_quantity":0}"""), Guid.NewGuid(), wallet.TargetUser, "Historical synthetic brand");
    private static VerifiedImportManifest Manifest(CheckoutFixture f, LegacyWalletSnapshot wallet, LegacyHistoricalOrder order)
    {
        byte[] body = VerifiedImportManifest.EncodePlan(SchemaManifest.ForQualification(f.Database.Data.ConnectionString), "public-a", f.Brands[0], "66371a23a1c8149dc5bdf89a0355aa689d454dba", SHA256.HashData("synthetic schema"u8), "protected:synthetic/source", SHA256.HashData("synthetic source"u8), Guid.NewGuid(), wallet.Proofs(f.Brands[0]).Concat(order.Proofs(f.Brands[0])), wallet.BalanceUsd);
        return VerifiedImportManifest.Open("protected:synthetic/manifest", SHA256.HashData(body), body);
    }
    [Fact]
    public async Task HistoricalOrderAndAliasCommitTogetherWithoutSpendingOrSendingAndDetectTargetDrift()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await CheckoutFixture.CreateAsync(users: 0, stock: 0, ct: ct);
        await MigrationQualification.EnableRoleAsync(f, ct);
        var wallet = MigrationQualification.Wallet() with { LegacyUser = "99001234" };
        var order = Order(wallet); wallet = wallet with { History = wallet.History.Append(new LegacyHistoryRow(order.Source.Id, Guid.NewGuid(), -order.Source.Total, "USD", order.Source.CreatedAt, null)).ToArray() }; var manifest = Manifest(f, wallet, order);
        await new OpeningBalanceImportWorkflow(f.Sessions).ImportAsync(f.Context(0), manifest, wallet, ct);
        var importer = new HistoricalOrderImportWorkflow(f.Sessions);
        await importer.ImportAsync(f.Context(0), manifest, order, ct);
        await importer.ImportAsync(f.Context(0), manifest, order, ct);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE product_id IS NULL AND fulfillment_status='requires_admin' AND payment_status='paid' AND quantity=2 AND unit_price_usdt=6.25", ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.legacy_id_map WHERE source_key_kind='public_order_reference' AND legacy_id='ORD-ABC123ABC123'", ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE is_posting AND movement_kind='opening_balance'", ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.inventory_items", ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments", ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.jobs", ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries", ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallets WHERE balance=12.34 AND version=1", ct));
        var reports = new ReconciliationReport(f.Sessions);
        var report = await reports.CompareOrdersAsync(manifest, [order], ct);
        Assert.True(report.Matched); Assert.False(report.ReleaseReady);
        string json = System.Text.Json.JsonSerializer.Serialize(report, MigrationReportJsonContext.Default.HistoricalOrderReconciliation);
        Assert.DoesNotContain(order.Source.Id, json, StringComparison.Ordinal);
        Assert.DoesNotContain(order.HistoricalBrandName, json, StringComparison.Ordinal);
        Assert.Equal(1L, (await reports.ReadAsync(manifest, ct)).PaidUnfulfilledOrders);
        Assert.False((await reports.CompareOrdersAsync(manifest, [], ct)).Matched);
        await Assert.ThrowsAsync<MigrationRejectedException>(() => importer.ImportAsync(f.Context(0), manifest, order with { HistoricalBrandName = "changed" }, ct));
        await using (var extra = f.Database.Data.CreateCommand("INSERT INTO shop.legacy_id_map(source_brand,source_table,source_key_kind,legacy_id,target_kind,target_id,target_brand_id,source_fingerprint,import_batch,import_manifest_ref) SELECT source_brand,source_table,source_key_kind,'ORD-EXTRA',target_kind,target_id,target_brand_id,source_fingerprint,import_batch,import_manifest_ref FROM shop.legacy_id_map WHERE source_table='orders' AND source_key_kind='primary'")) await extra.ExecuteNonQueryAsync(ct);
        var extraReport = await reports.CompareOrdersAsync(manifest, [order], ct);
        Assert.False(extraReport.Matched); Assert.Equal(1, extraReport.UnexpectedMappings);
        await using (var change = f.Database.Data.CreateCommand("UPDATE shop.orders SET fulfillment_status='waiting_stock'")) await change.ExecuteNonQueryAsync(ct);
        Assert.False((await reports.CompareOrdersAsync(manifest, [order], ct)).Matched);
        await Assert.ThrowsAsync<MigrationRejectedException>(() => importer.ImportAsync(f.Context(0), manifest, order, ct));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoricalWalletOrderRequiresMatchingOriginalDebitEvidence(bool wrongAmount)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await CheckoutFixture.CreateAsync(users: 0, stock: 0, ct: ct); await MigrationQualification.EnableRoleAsync(f, ct);
        var wallet = MigrationQualification.Wallet() with { LegacyUser = "99001234" }; var order = Order(wallet);
        if (wrongAmount) wallet = wallet with { History = wallet.History.Append(new LegacyHistoryRow(order.Source.Id, Guid.NewGuid(), -12.49m, "USD", order.Source.CreatedAt, null)).ToArray() };
        var manifest = Manifest(f, wallet, order); await new OpeningBalanceImportWorkflow(f.Sessions).ImportAsync(f.Context(0), manifest, wallet, ct);
        await Assert.ThrowsAsync<MigrationRejectedException>(() => new HistoricalOrderImportWorkflow(f.Sessions).ImportAsync(f.Context(0), manifest, order, ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders", ct)); Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.legacy_id_map WHERE source_table='orders'", ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE is_posting", ct));
    }
    [Theory]
    [InlineData("currency")]
    [InlineData("method")]
    [InlineData("quantity")]
    [InlineData("total")]
    [InlineData("discount")]
    [InlineData("percent")]
    [InlineData("savings")]
    [InlineData("toman")]
    [InlineData("alias")]
    [InlineData("snapshot")]
    public void UnsupportedOrInconsistentSourceCannotProduceImportProof(string fault)
    {
        var order = Order(MigrationQualification.Wallet()); var source = order.Source;
        source = fault switch
        {
            "currency" => source with { Currency = "IRR" },
            "method" => source with { CheckoutSnapshot = source.CheckoutSnapshot.Replace("wallet", "crypto", StringComparison.Ordinal) },
            "quantity" => source with { Quantity = 3 },
            "total" => source with { Total = 10 },
            "discount" => source with { CheckoutSnapshot = source.CheckoutSnapshot.Replace("\"discount_code\":null", "\"discount_code\":\"SHARED\"", StringComparison.Ordinal) },
            "percent" => source with { CheckoutSnapshot = source.CheckoutSnapshot.Replace("\"discount_pct\":null", "\"discount_pct\":\"5\"", StringComparison.Ordinal) },
            "savings" => source with { CheckoutSnapshot = source.CheckoutSnapshot.Replace("\"savings_usd\":\"0.00\"", "\"savings_usd\":\"1.00\"", StringComparison.Ordinal) },
            "toman" => source with { CheckoutSnapshot = source.CheckoutSnapshot.Replace("\"total_toman\":\"500000\"", "\"total_toman\":\"450000\"", StringComparison.Ordinal) },
            "alias" => source with { Id = "ORD-SYNTHETIC" },
            _ => source with { CheckoutSnapshot = "{}" }
        };
        Assert.Throws<MigrationRejectedException>(() => (order with { Source = source }).Proofs(Guid.NewGuid()));
        Assert.Equal("LegacyHistoricalOrder [redacted]", order.ToString());
    }
}
