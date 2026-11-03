using System.Security.Cryptography;
using System.Text.Json;

namespace Platform.Security;

public enum SecretService { Api, BotHost, JobHost }
public enum SecretConfigurationFailure { InvalidConfiguration, UnavailableFile, UnsafeFile, UnauthorizedScope, IdentityMismatch, ReusedKeyMaterial }

public sealed class SecretConfigurationException : Exception
{
    internal SecretConfigurationException(SecretConfigurationFailure failure) : base("Secret configuration rejected.") => Failure = failure;
    public SecretConfigurationFailure Failure { get; }
}

/// <summary>Trusted bootstrap authorization, supplied independently of the manifest being loaded.</summary>
public sealed class SecretLoadGrant
{
    internal HashSet<Guid> Brands { get; } = [];
    public SecretService Service { get; }
    public Guid DeploymentId { get; }
    public long Revision { get; }
    public bool AllowInventory { get; }

    public SecretLoadGrant(SecretService service, Guid deploymentId, long revision, IEnumerable<Guid> brands, bool allowInventory)
    {
        ArgumentNullException.ThrowIfNull(brands);
        if (service is not (SecretService.Api or SecretService.BotHost or SecretService.JobHost) ||
            deploymentId == Guid.Empty || revision <= 0 || (allowInventory && service != SecretService.JobHost))
            throw new ArgumentException("Invalid service secret authorization.");
        foreach (var brand in brands)
        {
            if (brand == Guid.Empty || !Brands.Add(brand) || Brands.Count > 32)
                throw new ArgumentException("Invalid service secret authorization.");
        }
        if (Brands.Count == 0 || (service == SecretService.BotHost && Brands.Count != 1))
            throw new ArgumentException("Invalid service secret authorization.");
        Service = service;
        DeploymentId = deploymentId;
        Revision = revision;
        AllowInventory = allowInventory;
    }
}

public delegate TResult SecretKeyOperation<TResult>(ReadOnlySpan<byte> key);

/// <summary>Owned immutable key snapshot. Trusted synchronous cipher callbacks only; never log or return raw keys.</summary>
public sealed class SecretSnapshot : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, LoadedKeyRing> brands;
    private readonly LoadedKeyRing? inventory;
    private readonly byte[][] material;
    private bool disposed;
    public Guid DeploymentId { get; }
    public long Revision { get; }
    public SecretService Service { get; }

    internal SecretSnapshot(SecretLoadGrant grant, Dictionary<Guid, LoadedKeyRing> brands, LoadedKeyRing? inventory, byte[][] material)
    {
        DeploymentId = grant.DeploymentId;
        Revision = grant.Revision;
        Service = grant.Service;
        this.brands = brands;
        this.inventory = inventory;
        this.material = material;
    }

    public string CurrentBrandKeyId(BrandContext context)
    {
        lock (gate) return BrandRing(context).CurrentKeyId;
    }

    public string CurrentInventoryKeyId
    {
        get { lock (gate) return InventoryRing().CurrentKeyId; }
    }

    public TResult UseBrandKey<TResult>(BrandContext context, string keyId, SecretKeyOperation<TResult> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (gate) return operation(Key(BrandRing(context), keyId));
    }

    public TResult UseInventoryKey<TResult>(string keyId, SecretKeyOperation<TResult> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (gate) return operation(Key(InventoryRing(), keyId));
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            foreach (var key in material) CryptographicOperations.ZeroMemory(key);
            brands.Clear();
            disposed = true;
        }
    }

    public override string ToString() => "Secret configuration (redacted)";

    private LoadedKeyRing BrandRing(BrandContext context)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(context);
        if (context.DeploymentId != DeploymentId || context.ConfigurationRevision != Revision ||
            !brands.TryGetValue(context.BrandId, out var ring))
            throw new SecretConfigurationException(SecretConfigurationFailure.UnauthorizedScope);
        return ring;
    }

    private LoadedKeyRing InventoryRing()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return inventory ?? throw new SecretConfigurationException(SecretConfigurationFailure.UnauthorizedScope);
    }

    private static byte[] Key(LoadedKeyRing ring, string keyId) => ring.Keys.TryGetValue(keyId, out var key)
        ? key : throw new SecretConfigurationException(SecretConfigurationFailure.UnauthorizedScope);
}

internal sealed record LoadedKeyRing(string CurrentKeyId, Dictionary<string, byte[]> Keys);
internal sealed record KeyReference(string Id, string File);
internal sealed record KeyRingReference(string CurrentKeyId, KeyReference[] Keys);

/// <summary>
/// Explicit Linux load: no environment, database, inline key, watcher or fallback provider. A complete
/// authorized revision must validate before a snapshot is returned; partial loads are zeroed on failure.
/// </summary>
public static class SecretFiles
{
    public static SecretSnapshot Load(string directory, string manifestFile, SecretLoadGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var material = new List<byte[]>();
        bool transferred = false;
        try
        {
            using var files = new LinuxSecretDirectory(directory);
            var manifest = files.Read(manifestFile, 65536);
            try
            {
                using var document = JsonDocument.Parse(manifest, new JsonDocumentOptions { MaxDepth = 8 });
                var root = Object(document.RootElement, "schemaVersion", "revision", "deploymentId", "service", "brands", "inventory");
                if (root["schemaVersion"].GetInt32() != 1) Invalid();
                if (root["revision"].GetInt64() != grant.Revision || root["deploymentId"].GetGuid() != grant.DeploymentId ||
                    root["service"].GetString() != grant.Service.ToString())
                    throw new SecretConfigurationException(SecretConfigurationFailure.IdentityMismatch);

                var brandReferences = new Dictionary<Guid, KeyRingReference>();
                var brandArray = root["brands"];
                if (brandArray.ValueKind != JsonValueKind.Array || brandArray.GetArrayLength() is < 1 or > 32) Invalid();
                foreach (var element in brandArray.EnumerateArray())
                {
                    var fields = Object(element, "brandId", "currentKeyId", "keys");
                    var brand = fields["brandId"].GetGuid();
                    if (!grant.Brands.Contains(brand) || !brandReferences.TryAdd(brand, ParseRing(fields)))
                        throw new SecretConfigurationException(SecretConfigurationFailure.UnauthorizedScope);
                }
                if (!grant.Brands.SetEquals(brandReferences.Keys))
                    throw new SecretConfigurationException(SecretConfigurationFailure.UnauthorizedScope);

                KeyRingReference? inventoryReference = null;
                if (root["inventory"].ValueKind != JsonValueKind.Null)
                {
                    if (!grant.AllowInventory) throw new SecretConfigurationException(SecretConfigurationFailure.UnauthorizedScope);
                    inventoryReference = ParseRing(Object(root["inventory"], "currentKeyId", "keys"));
                }
                if (grant.AllowInventory != (inventoryReference is not null))
                    throw new SecretConfigurationException(SecretConfigurationFailure.UnauthorizedScope);

                var usedFiles = new HashSet<string>(StringComparer.Ordinal) { manifestFile };
                LoadedKeyRing LoadRing(KeyRingReference reference)
                {
                    var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                    foreach (var entry in reference.Keys)
                    {
                        if (!usedFiles.Add(entry.File)) Invalid();
                        var key = files.Read(entry.File, 32);
                        material.Add(key);
                        if (key.Length != 32) Invalid();
                        if (material.Take(material.Count - 1).Any(previous => CryptographicOperations.FixedTimeEquals(previous, key)))
                            throw new SecretConfigurationException(SecretConfigurationFailure.ReusedKeyMaterial);
                        keys.Add(entry.Id, key);
                    }
                    return new LoadedKeyRing(reference.CurrentKeyId, keys);
                }

                var brands = new Dictionary<Guid, LoadedKeyRing>();
                foreach (var reference in brandReferences) brands.Add(reference.Key, LoadRing(reference.Value));
                var inventory = inventoryReference is null ? null : LoadRing(inventoryReference);
                var snapshot = new SecretSnapshot(grant, brands, inventory, material.ToArray());
                transferred = true;
                return snapshot;
            }
            finally { CryptographicOperations.ZeroMemory(manifest); }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        {
            throw new SecretConfigurationException(SecretConfigurationFailure.InvalidConfiguration);
        }
        finally
        {
            if (!transferred) foreach (var key in material) CryptographicOperations.ZeroMemory(key);
        }
    }

    internal static bool IsFileName(string value) => !string.IsNullOrEmpty(value) && value.Length <= 160 &&
        char.IsAsciiLetterOrDigit(value[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    internal static Dictionary<string, JsonElement> Object(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) Invalid();
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Contains(property.Name, StringComparer.Ordinal) || !fields.TryAdd(property.Name, property.Value)) Invalid();
        }
        if (fields.Count != names.Length) Invalid();
        return fields;
    }

    private static KeyRingReference ParseRing(Dictionary<string, JsonElement> fields)
    {
        var current = fields["currentKeyId"].GetString();
        var array = fields["keys"];
        if (current is null || !IsFileName(current) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 or > 8) Invalid();
        var keys = new List<KeyReference>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in array.EnumerateArray())
        {
            var key = Object(entry, "id", "file");
            var id = key["id"].GetString();
            var file = key["file"].GetString();
            if (id is null || file is null || !IsFileName(id) || !IsFileName(file) || !ids.Add(id)) Invalid();
            keys.Add(new KeyReference(id!, file!));
        }
        if (!ids.Contains(current!)) Invalid();
        return new KeyRingReference(current!, keys.ToArray());
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Invalid() => throw new SecretConfigurationException(SecretConfigurationFailure.InvalidConfiguration);
}
