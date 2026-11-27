using System.Security.Claims;
using System.Text;
using Platform.Security;
using Platform.Testing;

namespace Platform.UnitTests;

public sealed class SecretFilesTests
{
    [Fact]
    public void LoadsCurrentAndHistoricalKeysWithoutExposingThemAsProperties()
    {
        using var files = new SyntheticSecretFiles();
        using var snapshot = RecoverySecretFiles.Load(files.Root, "service-config.json", files.Grant());
        var context = Context(SyntheticSecretFiles.BrandA);
        Assert.Equal(7, snapshot.Revision);
        Assert.Equal("current", snapshot.CurrentBrandKeyId(context));
        Assert.Equal(32, snapshot.UseBrandKey(context, "current", key => key.Length));
        Assert.Equal(32, snapshot.UseBrandKey(context, "old", key => key.Length));
        Assert.Equal("current", snapshot.CurrentInventoryKeyId);
        Assert.Equal(32, snapshot.UseInventoryKey("current", key => key.Length));
        var expected = File.ReadAllBytes(Path.Combine(files.Root, "a-current"));
        try
        {
            Assert.True(snapshot.UseBrandKey(context, "current", key =>
                System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected, key)));
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(expected); }
        Assert.Equal("Secret configuration (redacted)", snapshot.ToString());
        Assert.DoesNotContain("a-current", snapshot.ToString());
        snapshot.Dispose();
        Assert.Throws<ObjectDisposedException>(() => snapshot.CurrentBrandKeyId(context));
    }

    [Fact]
    public void BotCanLoadOnlyItsOwnBrandAndNeverInventory()
    {
        using var files = new SyntheticSecretFiles();
        files.WriteManifest(SecretService.BotHost, false, brands: [SyntheticSecretFiles.BrandA]);
        using var snapshot = RecoverySecretFiles.Load(files.Root, "service-config.json",
            files.Grant(SecretService.BotHost, false, brands: [SyntheticSecretFiles.BrandA]));
        Assert.Equal(32, snapshot.UseBrandKey(Context(SyntheticSecretFiles.BrandA), "current", key => key.Length));
        Assert.Throws<SecretConfigurationException>(() => snapshot.UseBrandKey(Context(SyntheticSecretFiles.BrandB), "current", key => key.Length));
        Assert.Throws<SecretConfigurationException>(() => snapshot.UseInventoryKey("current", key => key.Length));
        Assert.Throws<ArgumentException>(() => files.Grant(SecretService.BotHost, true, brands: [SyntheticSecretFiles.BrandA]));
        Assert.Throws<ArgumentException>(() => files.Grant(SecretService.Api, true));
        Assert.Throws<ArgumentException>(() => files.Grant(SecretService.BotHost, false));
    }

    [Fact]
    public void ManifestCannotGrantItselfExtraScopeOrAnotherServiceIdentity()
    {
        using var files = new SyntheticSecretFiles();
        Assert.Throws<SecretConfigurationException>(() => RecoverySecretFiles.Load(files.Root, "service-config.json",
            files.Grant(inventory: false)));
        Assert.Throws<SecretConfigurationException>(() => RecoverySecretFiles.Load(files.Root, "service-config.json",
            files.Grant(brands: [SyntheticSecretFiles.BrandA])));
        Assert.Throws<SecretConfigurationException>(() => RecoverySecretFiles.Load(files.Root, "service-config.json",
            files.Grant(SecretService.Api, false)));
    }

    [Fact]
    public void DeploymentRevisionAndContextMustMatchExactly()
    {
        using var files = new SyntheticSecretFiles();
        Assert.Throws<SecretConfigurationException>(() => RecoverySecretFiles.Load(files.Root, "service-config.json", files.Grant(revision: 8)));
        Assert.Throws<SecretConfigurationException>(() => RecoverySecretFiles.Load(files.Root, "service-config.json", files.Grant(deployment: Guid.NewGuid())));
        using var snapshot = RecoverySecretFiles.Load(files.Root, "service-config.json", files.Grant());
        Assert.Throws<SecretConfigurationException>(() => snapshot.CurrentBrandKeyId(Context(SyntheticSecretFiles.BrandA, revision: 6)));
        Assert.Throws<SecretConfigurationException>(() => snapshot.CurrentBrandKeyId(Context(SyntheticSecretFiles.BrandA, deployment: Guid.NewGuid())));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/etc/private-key")]
    [InlineData("a/current")]
    [InlineData("..")]
    [InlineData("a\\current")]
    public void ReferencesCannotEscapeTheSecretDirectory(string reference)
    {
        using var files = new SyntheticSecretFiles();
        files.WriteManifest(aFile: reference);
        SafeReject(files);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(4096)]
    public void KeyFilesMustContainExactly32BinaryBytes(int length)
    {
        using var files = new SyntheticSecretFiles();
        files.Write("a-current", new byte[length]);
        SafeReject(files);
    }

    [Fact]
    public void MissingKeysAndUnknownCurrentIdsHaveNoFallback()
    {
        using var files = new SyntheticSecretFiles();
        File.Delete(Path.Combine(files.Root, "a-current"));
        SafeReject(files);
        files.Write("a-current", System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        files.WriteManifest(activeKey: "missing");
        SafeReject(files);
    }

    [Fact]
    public void ReusedKeyMaterialAndDuplicateFilesAcrossScopesAreRejected()
    {
        using var files = new SyntheticSecretFiles();
        var bytes = File.ReadAllBytes(Path.Combine(files.Root, "a-current"));
        try { files.Write("inventory-current", bytes); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
        SafeReject(files);
        files.WriteManifest(aFile: "b-current");
        SafeReject(files);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1}")]
    [InlineData("{\"rawKey\":\"private-payload\"}")]
    [InlineData("[]")]
    [InlineData("{")]
    public void SchemaUnknownDuplicateAndSecretBearingPropertiesAreRejected(string json)
    {
        using var files = new SyntheticSecretFiles();
        files.Write("service-config.json", Encoding.UTF8.GetBytes(json));
        SafeReject(files);
    }

    [Fact]
    public void OversizedManifestsAndWrongPermissionModesAreRejected()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        using var files = new SyntheticSecretFiles();
        File.SetUnixFileMode(Path.Combine(files.Root, "a-current"), UnixFileMode.UserRead | UnixFileMode.OtherRead);
        SafeReject(files);
        File.SetUnixFileMode(Path.Combine(files.Root, "a-current"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        SafeReject(files);
        File.SetUnixFileMode(Path.Combine(files.Root, "a-current"), UnixFileMode.UserRead);
        files.Write("service-config.json", new byte[65537]);
        SafeReject(files);
        files.WriteManifest();
        File.SetUnixFileMode(files.Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherWrite);
        SafeReject(files);
    }

    [Fact]
    public void SymlinkFilesAndDirectoriesAreRejected()
    {
        using var files = new SyntheticSecretFiles();
        File.Delete(Path.Combine(files.Root, "a-current"));
        File.CreateSymbolicLink(Path.Combine(files.Root, "a-current"), Path.Combine(files.Root, "b-current"));
        SafeReject(files);
        string alias = files.Root + "-alias";
        Directory.CreateSymbolicLink(alias, files.Root);
        try { Assert.Throws<SecretConfigurationException>(() => RecoverySecretFiles.Load(alias, "service-config.json", files.Grant())); }
        finally { Directory.Delete(alias); }
    }

    [Fact]
    public void ExistingSnapshotRemainsStableUntilExplicitNewRevisionLoad()
    {
        using var files = new SyntheticSecretFiles();
        using var previous = RecoverySecretFiles.Load(files.Root, "service-config.json", files.Grant());
        var fingerprint = previous.UseBrandKey(Context(SyntheticSecretFiles.BrandA), "current", key => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(key)));
        files.Write("a-current", System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        files.WriteManifest(revision: 8);
        Assert.Equal(fingerprint, previous.UseBrandKey(Context(SyntheticSecretFiles.BrandA), "current", key => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(key))));
        using var next = RecoverySecretFiles.Load(files.Root, "service-config.json", files.Grant(revision: 8));
        Assert.NotEqual(fingerprint, next.UseBrandKey(Context(SyntheticSecretFiles.BrandA, revision: 8), "current", key => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(key))));
    }

    [Fact]
    public void CompleteManifestRejectsVersionDuplicateFieldsAndInlineKeys()
    {
        using var files = new SyntheticSecretFiles();
        string valid = File.ReadAllText(Path.Combine(files.Root, "service-config.json"));
        string[] invalid =
        [
            valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal),
            valid[..^1] + ",\"revision\":7}",
            valid[..^1] + ",\"rawKey\":\"private-payload\"}",
            valid.Replace("\"id\":\"old\"", "\"id\":\"current\"", StringComparison.Ordinal)
        ];
        foreach (var json in invalid)
        {
            files.Write("service-config.json", Encoding.UTF8.GetBytes(json));
            SafeReject(files);
        }
    }

    [Fact]
    public async Task FifoCannotBlockSecretLoading()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        using var files = new SyntheticSecretFiles();
        string path = Path.Combine(files.Root, "a-current");
        File.Delete(path);
        var start = new System.Diagnostics.ProcessStartInfo("mkfifo") { UseShellExecute = false };
        start.ArgumentList.Add(path);
        using var process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0, process.ExitCode);
        File.SetUnixFileMode(path, UnixFileMode.UserRead);
        await Task.Run(() => SafeReject(files), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    private static void SafeReject(SyntheticSecretFiles files)
    {
        var error = Assert.Throws<SecretConfigurationException>(() => RecoverySecretFiles.Load(files.Root, "service-config.json", files.Grant()));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(files.Root, error.ToString());
        Assert.DoesNotContain("private-payload", error.ToString());
    }

    private static BrandContext Context(Guid brand, long revision = 7, Guid? deployment = null)
    {
        var registry = new BotBrandRegistry([new("test-bot", "a", brand, 101, deployment ?? SyntheticSecretFiles.Deployment, revision, true)]);
        Assert.True(registry.TryResolveAuthenticated("test-bot", new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "a")], "test-bot")), out var context));
        return context!;
    }
}
