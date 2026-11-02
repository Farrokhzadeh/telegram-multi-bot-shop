using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Platform.Migration;
using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class LegacyMigrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task ManifestMappingsAndFrozenOpeningBalancesQualifyInNativeCompatibleWorkflow() => await MigrationQualification.RunAsync(Ct);
    [Fact]
    public async Task OldKeyGoldenFixtureReencryptsToCurrentBrandAndSeparateInventoryWithoutPlaintextExport()
    {
        await using var f = await CheckoutFixture.CreateAsync(users: 0, stock: 0, ct: Ct);
        using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "legacy-fernet-synthetic.json")));
        var v = vectors.RootElement.GetProperty("vectors").EnumerateArray().First(x => x.GetProperty("kind").GetString() == "Secret" && x.GetProperty("keys").GetArrayLength() > 1);
        string[] keys = v.GetProperty("keys").EnumerateArray().Select(x => x.GetString()!).ToArray();
        string source = v.GetProperty("ciphertext").GetString()!;
        byte[] expected = Encoding.UTF8.GetBytes(v.GetProperty("plaintext").GetString()!);
        Guid row = Guid.NewGuid();
        var workflow = new ReencryptionWorkflow(f.Secrets);
        var brand = workflow.Brand(f.Context(0), row, MigrationCipherField.FulfillmentContent, source, LegacyFieldKind.Secret, keys);
        byte[] opened = new BrandCipher(f.Secrets).Decrypt(f.Context(0), "fulfillments", row, "delivered_content", brand.KeyId, brand.Ciphertext.Span);
        try { Assert.Equal(expected, opened); } finally { CryptographicOperations.ZeroMemory(opened); }
        Assert.Throws<CipherRejectedException>(() => new BrandCipher(f.Secrets).Decrypt(f.Context(1), "fulfillments", row, "delivered_content", brand.KeyId, brand.Ciphertext.Span));
        Assert.Throws<CryptographicException>(() => workflow.Brand(f.Context(0), row, MigrationCipherField.FulfillmentContent, source, LegacyFieldKind.Secret, ["wrong public key"]));
        var stock = workflow.Inventory(row, source, LegacyFieldKind.Secret, keys);
        opened = new InventoryCipher(f.Secrets).Decrypt(row, stock.KeyId, stock.Ciphertext.Span);
        try { Assert.Equal(expected, opened); } finally { CryptographicOperations.ZeroMemory(opened); }
        Assert.DoesNotContain(v.GetProperty("plaintext").GetString()!, brand.ToString());
        Assert.Throws<CipherRejectedException>(() => new BrandCipher(f.Secrets).Decrypt(f.Context(0), "inventory_items", row, "credential_ciphertext", stock.KeyId, stock.Ciphertext.Span));
        CryptographicOperations.ZeroMemory(expected);
    }
    [Fact]
    public async Task ManifestTamperDestinationAndIncompleteEntrySetsFailBeforeTargetWrites()
    {
        await using var f = await CheckoutFixture.CreateAsync(users: 0, stock: 0, ct: Ct);
        var row = MigrationQualification.Wallet();
        var manifest = MigrationQualification.Manifest(f, "public-a", f.Brands[0], [row]);
        byte[] changed = manifest.Encode(); changed[^1] ^= 1;
        Assert.Throws<MigrationRejectedException>(() => VerifiedImportManifest.Open(manifest.Reference, manifest.Digest.Span, changed));
        await Assert.ThrowsAsync<MigrationRejectedException>(() => new OpeningBalanceImportWorkflow(f.Sessions).ImportAsync(f.Context(1), manifest, row, Ct));
        var different = row with { BalanceUsd = row.BalanceUsd + 1 };
        await Assert.ThrowsAsync<MigrationRejectedException>(() => new OpeningBalanceImportWorkflow(f.Sessions).ImportAsync(f.Context(0), manifest, different, Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.users", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.legacy_id_map", Ct));
    }
    [Fact]
    public async Task PrimaryAndHistoricalPublicReferenceStayDistinctAndChangedReplayConflicts()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 100, stock: 1, ct: Ct);
        var purchase = await f.Store.PurchaseLocalAsync(f.Context(0), CheckoutFixture.Actor(0), f.Request(), Ct);
        await MigrationQualification.EnableRoleAsync(f, Ct);
        Guid order = purchase.OrderId!.Value;
        var primary = new LegacyMapProof(LegacyProjection.Order, "old-primary", order, f.Brands[0], SHA256.HashData("public primary"u8));
        var alias = new LegacyMapProof(LegacyProjection.OrderAlias, "old-primary", order, f.Brands[0], SHA256.HashData("public alias"u8));
        var manifest = MigrationQualification.Manifest(f, "public-a", f.Brands[0], [], [primary, alias]);
        await using (var session = await f.Sessions.BeginAsync(Ct))
        {
            Assert.True(await LegacyMapStore.EnsureAsync(session, manifest, primary, Ct));
            Assert.True(await LegacyMapStore.EnsureAsync(session, manifest, alias, Ct));
            await session.CommitAsync(Ct);
        }
        await using (var replay = await f.Sessions.BeginAsync(Ct))
        { Assert.False(await LegacyMapStore.EnsureAsync(replay, manifest, alias, Ct)); await replay.CommitAsync(Ct); }
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.legacy_id_map WHERE source_table='orders' AND legacy_id='old-primary'", Ct));
        var bad = new LegacyMapProof(LegacyProjection.OrderAlias, "old-primary", order, f.Brands[0], SHA256.HashData("changed public alias"u8));
        var changed = MigrationQualification.Manifest(f, "public-a", f.Brands[0], [], [bad]);
        await using var rejected = await f.Sessions.BeginAsync(Ct);
        await Assert.ThrowsAsync<MigrationRejectedException>(() => LegacyMapStore.EnsureAsync(rejected, changed, bad, Ct));
    }
}
