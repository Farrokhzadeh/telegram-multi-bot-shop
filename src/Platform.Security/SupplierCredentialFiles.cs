using System.Security.Cryptography;
using System.Text.Json;

namespace Platform.Security;

public sealed class SupplierCredentialGrant
{
    internal Dictionary<string, string> Accounts { get; } = new(StringComparer.Ordinal);
    public SecretService Service { get; }
    public Guid DeploymentId { get; }
    public long Revision { get; }
    public SupplierCredentialGrant(SecretService service, Guid deploymentId, long revision, IEnumerable<KeyValuePair<string, string>> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        if (service is not (SecretService.Api or SecretService.JobHost) || deploymentId == Guid.Empty || revision <= 0) throw new ArgumentException("Invalid supplier credential grant.");
        foreach (var account in accounts)
            if (!Label(account.Key) || !Label(account.Value) || !Accounts.TryAdd(account.Key, account.Value) || Accounts.Count > 16) throw new ArgumentException("One bounded account per provider is required.");
        if (Accounts.Count == 0) throw new ArgumentException("A supplier account grant is required.");
        Service = service; DeploymentId = deploymentId; Revision = revision;
    }
    private static bool Label(string? value) => value is { Length: > 0 and <= 160 } && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
}
public sealed class SupplierCredentialSnapshot : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, (string Account, byte[] Credential)> entries;
    private bool disposed;
    public SecretService Service { get; }
    public Guid DeploymentId { get; }
    public long Revision { get; }
    internal SupplierCredentialSnapshot(SupplierCredentialGrant grant, Dictionary<string, (string Account, byte[] Credential)> entries)
    { Service = grant.Service; DeploymentId = grant.DeploymentId; Revision = grant.Revision; this.entries = entries; }
    public KeyValuePair<string, string>[] Accounts
    {
        get { lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); return entries.Select(x => new KeyValuePair<string, string>(x.Key, x.Value.Account)).ToArray(); } }
    }
    public TResult UseCredential<TResult>(string provider, string expectedAccount, SecretKeyOperation<TResult> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!entries.TryGetValue(provider, out var entry) || entry.Account != expectedAccount) throw new SecretConfigurationException(SecretConfigurationFailure.UnauthorizedScope);
            return operation(entry.Credential);
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            foreach (var entry in entries.Values) CryptographicOperations.ZeroMemory(entry.Credential);
            entries.Clear(); disposed = true;
        }
    }
    public override string ToString() => "Supplier credentials (redacted)";
}
public static class SupplierCredentialFiles
{
    public static SupplierCredentialSnapshot Load(string directory, string manifestFile, SupplierCredentialGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var entries = new Dictionary<string, (string Account, byte[] Credential)>(StringComparer.Ordinal);
        bool transferred = false;
        try
        {
            using var files = new LinuxSecretDirectory(directory);
            byte[] manifest = files.Read(manifestFile, 65536);
            try
            {
                using var document = JsonDocument.Parse(manifest, new JsonDocumentOptions { MaxDepth = 4 });
                var root = SecretFiles.Object(document.RootElement, "schemaVersion", "deploymentId", "revision", "service", "accounts");
                if (root["schemaVersion"].GetInt32() != 1) Invalid();
                if (root["deploymentId"].GetGuid() != grant.DeploymentId || root["revision"].GetInt64() != grant.Revision || root["service"].GetString() != grant.Service.ToString())
                    throw new SecretConfigurationException(SecretConfigurationFailure.IdentityMismatch);
                var array = root["accounts"];
                if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() != grant.Accounts.Count) Invalid();
                var names = new HashSet<string>(StringComparer.Ordinal) { manifestFile };
                foreach (var item in array.EnumerateArray())
                {
                    var fields = SecretFiles.Object(item, "provider", "accountRef", "credentialFile", "credentialSha256");
                    string? provider = fields["provider"].GetString(), account = fields["accountRef"].GetString(), file = fields["credentialFile"].GetString();
                    if (provider is null || account is null || !grant.Accounts.TryGetValue(provider, out string? expected) || account != expected)
                        throw new SecretConfigurationException(SecretConfigurationFailure.UnauthorizedScope);
                    if (entries.ContainsKey(provider) || file is null || !names.Add(file)) Invalid();
                    byte[] credential = files.Read(file, 8192);
                    bool retained = false;
                    try
                    {
                        byte[] expectedHash = Convert.FromHexString(fields["credentialSha256"].GetString() ?? "");
                        if (expectedHash.Length != 32) Invalid();
                        if (!CryptographicOperations.FixedTimeEquals(expectedHash, SHA256.HashData(credential))) throw new SecretConfigurationException(SecretConfigurationFailure.IdentityMismatch);
                        entries.Add(provider, (account, credential)); retained = true;
                    }
                    finally { if (!retained) CryptographicOperations.ZeroMemory(credential); }
                }
                var snapshot = new SupplierCredentialSnapshot(grant, entries);
                transferred = true; return snapshot;
            }
            finally { CryptographicOperations.ZeroMemory(manifest); }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        { throw new SecretConfigurationException(SecretConfigurationFailure.InvalidConfiguration); }
        finally { if (!transferred) foreach (var entry in entries.Values) CryptographicOperations.ZeroMemory(entry.Credential); }
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Invalid() => throw new SecretConfigurationException(SecretConfigurationFailure.InvalidConfiguration);
}
