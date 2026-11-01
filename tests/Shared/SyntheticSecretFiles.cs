using System.Security.Cryptography;
using System.Text.Json;
using Platform.Security;

namespace Platform.Testing;

/// <summary>Disposable generated keys only. Never reads environment key paths or source credentials.</summary>
public sealed class SyntheticSecretFiles : IDisposable
{
    public static readonly Guid BrandA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    public static readonly Guid BrandB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    public static readonly Guid Deployment = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "shop-secret-qualification-" + Guid.NewGuid().ToString("N"));

    public SyntheticSecretFiles()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux qualification required.");
        Directory.CreateDirectory(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var name in new[] { "a-current", "a-old", "b-current", "c-current", "inventory-current" })
        {
            var bytes = RandomNumberGenerator.GetBytes(32);
            try { Write(name, bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        WriteManifest();
    }

    public SecretLoadGrant Grant(SecretService service = SecretService.JobHost, bool inventory = true,
        long revision = 7, Guid? deployment = null, Guid[]? brands = null) =>
        new(service, deployment ?? Deployment, revision, brands ?? [BrandA, BrandB], inventory);

    public void WriteManifest(SecretService service = SecretService.JobHost, bool inventory = true, long revision = 7,
        string activeKey = "current", string aFile = "a-current", Guid[]? brands = null)
    {
        brands ??= [BrandA, BrandB];
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteNumber("revision", revision);
            writer.WriteString("deploymentId", Deployment);
            writer.WriteString("service", service.ToString());
            writer.WriteStartArray("brands");
            foreach (var brand in brands)
            {
                writer.WriteStartObject();
                writer.WriteString("brandId", brand);
                writer.WriteString("currentKeyId", brand == BrandA ? activeKey : "current");
                writer.WriteStartArray("keys");
                WriteReference(writer, "current", brand == BrandA ? aFile : brand == BrandB ? "b-current" : "c-current");
                if (brand == BrandA) WriteReference(writer, "old", "a-old");
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (inventory)
            {
                writer.WriteStartObject("inventory");
                writer.WriteString("currentKeyId", "current");
                writer.WriteStartArray("keys");
                WriteReference(writer, "current", "inventory-current");
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            else writer.WriteNull("inventory");
            writer.WriteEndObject();
        }
        Write("service-config.json", bytes.ToArray());
    }

    public void Write(string name, byte[] bytes)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var path = Path.Combine(Root, name);
        if (File.Exists(path)) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllBytes(path, bytes);
        File.SetUnixFileMode(path, UnixFileMode.UserRead);
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);

    private static void WriteReference(Utf8JsonWriter writer, string id, string file)
    {
        writer.WriteStartObject();
        writer.WriteString("id", id);
        writer.WriteString("file", file);
        writer.WriteEndObject();
    }
}
