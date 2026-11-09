using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Platform.Security;
using Platform.Telegram;

namespace Platform.BotHost;

public sealed record StagingHistoryBridgeResponse(string[] Messages, int? NextOffset, int SourceOrders, bool SyntheticOnly);

[JsonSerializable(typeof(StagingHistoryBridgeResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class StagingHistoryBridgeJson : JsonSerializerContext
{
}

internal static class StagingHistoryBridge
{
    private const string BindingName = "stage-binding.json";
    private const string ManifestName = "service-config.json";
    private const string KeyName = "stage-key";
    private const string HistoryName = "teleg-customer-history-synthetic";
    private const int FixtureCount = 55;

    private static void RequireRoot(string root, bool mustExist)
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("INSTANCE_ID") != "staging" ||
            !Path.IsPathFullyQualified(root) || !root.StartsWith("/app/data/teleg-staging-history-", StringComparison.Ordinal) ||
            root.Contains("/../", StringComparison.Ordinal) || root.Length > 200 ||
            (mustExist && (!Directory.Exists(root) || new DirectoryInfo(root).LinkTarget is not null ||
                (File.GetUnixFileMode(root) & (UnixFileMode)63) != 0)))
            throw new UnauthorizedAccessException("Staging history bridge rejected.");
    }
    private static string ReadPrivate(string path, int limit)
    {
        if (!OperatingSystem.IsLinux()) throw new UnauthorizedAccessException("Staging history bridge rejected.");
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || info.Length is < 1 or > 16384 ||
            info.Length > limit || (File.GetUnixFileMode(path) & (UnixFileMode)63) != 0)
            throw new UnauthorizedAccessException("Staging history bridge rejected.");
        return File.ReadAllText(path);
    }
    private static void WritePrivate(string path, ReadOnlySpan<byte> data)
    {
        if (!OperatingSystem.IsLinux()) throw new UnauthorizedAccessException("Staging history bridge rejected.");
        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
        stream.Write(data);
        stream.Flush(true);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    private static BrandContext Context(Guid brand, Guid deployment, long bot)
    {
        var registry = new BotBrandRegistry([new("staging-bridge", "testadbadbot", brand, bot, deployment, 1, true)]);
        if (!registry.TryResolveAuthenticated("staging-bridge",
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "testadbadbot")], "staging-bridge")), out var context))
            throw new UnauthorizedAccessException("Staging history bridge rejected.");
        return context!;
    }
    private static CustomerHistoricalOrder Synthetic(int i) => new(
        "STAGE-SYNTH-" + i.ToString("D6", CultureInfo.InvariantCulture),
        new DateTime(2024, 2, 1, 12, 0, 0, DateTimeKind.Utc).AddMinutes(i),
        2, i % 2 == 0 ? 600000m : 12.25m, i % 2 == 0 ? "IRR" : "USD", null, null, true);

    internal static async Task<int> InitializeAsync(string root, string actorText, string botText, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux()) throw new UnauthorizedAccessException("Staging history bridge rejected.");
        RequireRoot(root, false);
        if (Environment.GetEnvironmentVariable("SHOP_ALLOW_STAGING_HISTORY_FIXTURE") != "1" ||
            !long.TryParse(actorText, NumberStyles.None, CultureInfo.InvariantCulture, out long actor) || actor <= 0 ||
            !long.TryParse(botText, NumberStyles.None, CultureInfo.InvariantCulture, out long bot) || bot <= 0 ||
            Directory.Exists(root))
            throw new UnauthorizedAccessException("Staging history bridge rejected.");
        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var brand = Guid.NewGuid(); var deployment = Guid.NewGuid(); var member = Guid.NewGuid();
        byte[] key = RandomNumberGenerator.GetBytes(32);
        try { WritePrivate(Path.Combine(root, KeyName), key); }
        finally { CryptographicOperations.ZeroMemory(key); }
        using (var memory = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(memory))
            {
                writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 1);
                writer.WriteNumber("revision", 1); writer.WriteString("deploymentId", deployment);
                writer.WriteString("service", "BotHost"); writer.WriteStartArray("brands");
                writer.WriteStartObject(); writer.WriteString("brandId", brand);
                writer.WriteString("currentKeyId", "stage");
                writer.WriteStartArray("keys"); writer.WriteStartObject();
                writer.WriteString("id", "stage"); writer.WriteString("file", KeyName);
                writer.WriteEndObject(); writer.WriteEndArray(); writer.WriteEndObject(); writer.WriteEndArray();
                writer.WriteNull("inventory"); writer.WriteEndObject();
            }
            WritePrivate(Path.Combine(root, ManifestName), memory.ToArray());
        }
        using (var memory = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(memory))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", 1);
                writer.WriteString("instanceId", "staging"); writer.WriteString("botUsername", "testadbadbot");
                writer.WriteString("brandId", brand); writer.WriteString("deploymentId", deployment);
                writer.WriteString("membershipId", member); writer.WriteNumber("actorId", actor);
                writer.WriteNumber("botId", bot); writer.WriteNumber("originalOrders", FixtureCount);
                writer.WriteBoolean("syntheticOnly", true); writer.WriteEndObject();
            }
            WritePrivate(Path.Combine(root, BindingName), memory.ToArray());
        }
        string dir = Path.Combine(root, HistoryName);
        Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var ctx = Context(brand, deployment, bot);
        using var secrets = SecretFiles.Load(root, ManifestName, new SecretLoadGrant(SecretService.BotHost, deployment, 1, [brand], false));
        var original = Enumerable.Range(0, FixtureCount).Select(Synthetic).ToArray();
        byte[] sealedBytes = new CustomerHistoryArchive(secrets).Seal(ctx, member, original);
        await new CustomerHistoryFiles(dir, secrets).PublishAsync(ctx, member, sealedBytes, ct);
        var reread = await new CustomerHistoryFiles(dir, secrets).ReadAsync(ctx, member, ct);
        if (new CustomerHistoryArchive(secrets).Open(ctx, member, reread.Span).Count != FixtureCount)
            throw new UnauthorizedAccessException("Staging history bridge rejected.");
        Console.WriteLine("PASS: staging-only encrypted synthetic 55-order history initialized; no network or live purchase.");
        return 0;
    }

    internal static async Task<int> RenderAsync(string root, string actorText, string botText, string command, CancellationToken ct = default)
    {
        RequireRoot(root, true);
        if (!long.TryParse(actorText, NumberStyles.None, CultureInfo.InvariantCulture, out long actor) || actor <= 0 ||
            !long.TryParse(botText, NumberStyles.None, CultureInfo.InvariantCulture, out long bot) || bot <= 0 ||
            !CustomerHistoryCommandRouter.TryParsePage(command, out int offset))
            throw new UnauthorizedAccessException("Staging history bridge rejected.");
        using var binding = JsonDocument.Parse(ReadPrivate(Path.Combine(root, BindingName), 4096));
        var info = binding.RootElement;
        if (info.EnumerateObject().Count() != 10 || info.GetProperty("schemaVersion").GetInt32() != 1 ||
            info.GetProperty("instanceId").GetString() != "staging" || info.GetProperty("botUsername").GetString() != "testadbadbot" ||
            !info.GetProperty("syntheticOnly").GetBoolean() || info.GetProperty("originalOrders").GetInt32() != FixtureCount ||
            info.GetProperty("actorId").GetInt64() != actor || info.GetProperty("botId").GetInt64() != bot)
            throw new UnauthorizedAccessException("Staging history bridge rejected.");
        var brand = info.GetProperty("brandId").GetGuid();
        var deployment = info.GetProperty("deploymentId").GetGuid();
        var member = info.GetProperty("membershipId").GetGuid();
        if (brand == Guid.Empty || deployment == Guid.Empty || member == Guid.Empty)
            throw new UnauthorizedAccessException("Staging history bridge rejected.");
        var ctx = Context(brand, deployment, bot);
        using var secrets = SecretFiles.Load(root, ManifestName, new SecretLoadGrant(SecretService.BotHost, deployment, 1, [brand], false));
        var sealedBytes = await new CustomerHistoryFiles(Path.Combine(root, HistoryName), secrets).ReadAsync(ctx, member, ct);
        var originals = new CustomerHistoryArchive(secrets).Open(ctx, member, sealedBytes.Span);
        if (originals.Count != FixtureCount || originals.Any(o => !o.OriginalReference.StartsWith("STAGE-SYNTH-", StringComparison.Ordinal)))
            throw new UnauthorizedAccessException("Staging history bridge rejected.");
        var imported = originals.Take(10).Select(o => o with { SavedTitle = "Verified synthetic product" }).ToArray();
        var page = CustomerHistoricalOrderDeduplicator.Page(originals, imported, offset);
        var messages = CustomerHistoryMessageRenderer.Render(page)
            .Select(part => "[STAGING TEST - SYNTHETIC ORDERS ONLY]\n" + part.Text).ToArray();
        if (messages.Any(m => m.Length > 3900))
            throw new UnauthorizedAccessException("Staging history bridge rejected.");
        Console.WriteLine(JsonSerializer.Serialize(new StagingHistoryBridgeResponse(messages, page.NextOffset, FixtureCount, true),
            StagingHistoryBridgeJson.Default.StagingHistoryBridgeResponse));
        return 0;
    }
}
