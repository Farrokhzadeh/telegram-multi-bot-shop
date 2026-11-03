using System.Security.Cryptography;
using System.Text.Json;
using Platform.Security;

namespace Platform.Testing;

public static class SupplierCredentialQualification
{
    public static SupplierCredentialGrant Grant(long revision = 7, SecretService service = SecretService.JobHost, string account = "public account", Guid? deployment = null) =>
        new(service, deployment ?? SyntheticSecretFiles.Deployment, revision, [new("synthetic", account)]);
    public static void Write(SyntheticSecretFiles files, long revision = 7, SecretService service = SecretService.JobHost, Guid? deployment = null)
    {
        byte[] token = RandomNumberGenerator.GetBytes(64);
        string digest = Convert.ToHexString(SHA256.HashData(token));
        try { files.Write("supplier-token", token); }
        finally { CryptographicOperations.ZeroMemory(token); }
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 1); writer.WriteString("deploymentId", deployment ?? SyntheticSecretFiles.Deployment);
            writer.WriteNumber("revision", revision); writer.WriteString("service", service.ToString()); writer.WriteStartArray("accounts");
            writer.WriteStartObject(); writer.WriteString("provider", "synthetic"); writer.WriteString("accountRef", "public account"); writer.WriteString("credentialFile", "supplier-token");
            writer.WriteString("credentialSha256", digest);
            writer.WriteEndObject(); writer.WriteEndArray(); writer.WriteEndObject();
        }
        files.Write("supplier-config.json", bytes.ToArray());
    }
    public static void Run()
    {
        using var files = new SyntheticSecretFiles();
        Write(files);
        using var old = SupplierCredentialFiles.Load(files.Root, "supplier-config.json", Grant());
        byte[] previous = old.UseCredential("synthetic", "public account", key => SHA256.HashData(key));
        Write(files, 8);
        using var current = SupplierCredentialFiles.Load(files.Root, "supplier-config.json", Grant(8));
        if (old.Revision != 7 || current.Revision != 8 || !current.Accounts.Single().Equals(new KeyValuePair<string, string>("synthetic", "public account")) ||
            !old.UseCredential("synthetic", "public account", key => CryptographicOperations.FixedTimeEquals(previous, SHA256.HashData(key))) ||
            current.UseCredential("synthetic", "public account", key => CryptographicOperations.FixedTimeEquals(previous, SHA256.HashData(key))))
            throw new InvalidOperationException("Supplier credential snapshot rotation failed.");
        bool denied = false;
        try { current.UseCredential("synthetic", "another account", key => key.Length); }
        catch (SecretConfigurationException) { denied = true; }
        if (!denied || current.ToString() != "Supplier credentials (redacted)") throw new InvalidOperationException("Supplier credential scope failed.");
        current.Dispose(); denied = false;
        try { current.UseCredential("synthetic", "public account", key => key.Length); }
        catch (ObjectDisposedException) { denied = true; }
        if (!denied) throw new InvalidOperationException("Disposed supplier credential snapshot remained usable.");
    }
}
