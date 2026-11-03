using System.Text;
using Platform.Security;
using Platform.Testing;

namespace Platform.UnitTests;

public sealed class SupplierCredentialTests
{
    [Fact]
    public void NativeCompatiblePinnedSnapshotRotationAndScope() => SupplierCredentialQualification.Run();
    [Theory]
    [InlineData("schema", SecretConfigurationFailure.InvalidConfiguration)]
    [InlineData("revision", SecretConfigurationFailure.IdentityMismatch)]
    [InlineData("deployment", SecretConfigurationFailure.IdentityMismatch)]
    [InlineData("service", SecretConfigurationFailure.IdentityMismatch)]
    [InlineData("account", SecretConfigurationFailure.UnauthorizedScope)]
    [InlineData("provider", SecretConfigurationFailure.UnauthorizedScope)]
    [InlineData("extra", SecretConfigurationFailure.InvalidConfiguration)]
    [InlineData("duplicate", SecretConfigurationFailure.InvalidConfiguration)]
    [InlineData("inline", SecretConfigurationFailure.InvalidConfiguration)]
    [InlineData("missing", SecretConfigurationFailure.InvalidConfiguration)]
    [InlineData("path", SecretConfigurationFailure.UnsafeFile)]
    public void ManifestMustMatchTheCompleteIndependentGrant(string mutation, SecretConfigurationFailure failure)
    {
        using var files = new SyntheticSecretFiles();
        SupplierCredentialQualification.Write(files);
        string text = File.ReadAllText(Path.Combine(files.Root, "supplier-config.json"));
        text = mutation switch
        {
            "schema" => text.Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal),
            "revision" => text.Replace("\"revision\":7", "\"revision\":8", StringComparison.Ordinal),
            "deployment" => text.Replace(SyntheticSecretFiles.Deployment.ToString("D"), Guid.NewGuid().ToString("D"), StringComparison.Ordinal),
            "service" => text.Replace("JobHost", "Api", StringComparison.Ordinal),
            "account" => text.Replace("public account", "public other account", StringComparison.Ordinal),
            "provider" => text.Replace("synthetic", "public-other-provider", StringComparison.Ordinal),
            "extra" => text.Insert(1, "\"brands\":[],"),
            "duplicate" => text.Insert(1, "\"revision\":7,"),
            "inline" => text.Replace("\"credentialFile\":\"supplier-token\"", "\"credentialFile\":\"supplier-token\",\"credential\":\"public inline\"", StringComparison.Ordinal),
            "missing" => text.Replace("\"credentialFile\":\"supplier-token\"", "\"unapproved\":null", StringComparison.Ordinal),
            "path" => text.Replace("supplier-token", "../supplier-token", StringComparison.Ordinal),
            _ => throw new ArgumentException()
        };
        files.Write("supplier-config.json", Encoding.UTF8.GetBytes(text));
        var error = Assert.Throws<SecretConfigurationException>(() => SupplierCredentialFiles.Load(files.Root, "supplier-config.json", SupplierCredentialQualification.Grant()));
        Assert.Equal(failure, error.Failure);
        Assert.DoesNotContain(files.Root, error.ToString());
        Assert.DoesNotContain("public inline", error.ToString());
    }
    [Theory]
    [InlineData("writable")]
    [InlineData("link")]
    [InlineData("empty")]
    [InlineData("oversized")]
    [InlineData("missing")]
    [InlineData("directory")]
    [InlineData("changed")]
    public void CredentialFilesUseTheExistingNoFollowReadOnlyBoundedReader(string failure)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        using var files = new SyntheticSecretFiles();
        SupplierCredentialQualification.Write(files);
        string token = Path.Combine(files.Root, "supplier-token");
        if (failure == "writable") File.SetUnixFileMode(token, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        else if (failure == "empty") files.Write("supplier-token", []);
        else if (failure == "oversized") files.Write("supplier-token", new byte[8193]);
        else if (failure == "changed") files.Write("supplier-token", new byte[64]);
        else
        {
            File.Delete(token);
            if (failure == "link") File.CreateSymbolicLink(token, "a-current");
            else if (failure == "directory") Directory.CreateDirectory(token, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Assert.Throws<SecretConfigurationException>(() => SupplierCredentialFiles.Load(files.Root, "supplier-config.json", SupplierCredentialQualification.Grant()));
    }
    [Fact]
    public void GrantsRejectBotsAndAccountPoolsAndSnapshotMetadataCannotChangeScope()
    {
        Assert.Throws<ArgumentException>(() => SupplierCredentialQualification.Grant(service: SecretService.BotHost));
        Assert.Throws<ArgumentException>(() => new SupplierCredentialGrant(SecretService.JobHost, SyntheticSecretFiles.Deployment, 7, [new("synthetic", "a"), new("synthetic", "b")]));
        using var files = new SyntheticSecretFiles();
        SupplierCredentialQualification.Write(files, service: SecretService.Api);
        using var snapshot = SupplierCredentialFiles.Load(files.Root, "supplier-config.json", SupplierCredentialQualification.Grant(service: SecretService.Api));
        var metadata = snapshot.Accounts; metadata[0] = new("synthetic", "public other account");
        Assert.Equal(64, snapshot.UseCredential("synthetic", "public account", token => token.Length));
        Assert.Throws<SecretConfigurationException>(() => snapshot.UseCredential("synthetic", "public other account", token => token.Length));
    }
}
