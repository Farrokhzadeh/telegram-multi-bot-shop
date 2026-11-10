using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Platform.Persistence;
using Platform.Security;
using Platform.Telegram;

namespace Platform.BotHost;

internal static class StagingHistoryRuntime
{
	private static byte[] ReadPrivate(string path, int max)
	{
		var file = new FileInfo(path);
		if (!OperatingSystem.IsLinux() || !file.Exists || file.LinkTarget is not null ||
			file.Length is < 1 or > 1048576 || file.Length > max || (File.GetUnixFileMode(path) & (UnixFileMode)63) != 0)
			throw new UnauthorizedAccessException();
		return File.ReadAllBytes(path);
	}
	private static string ReadText(string path, int max)
	{
		string value = Encoding.UTF8.GetString(ReadPrivate(path, max));
		if (value.EndsWith('\n')) value = value[..^1];
		if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
			throw new UnauthorizedAccessException();
		return value;
	}
	internal static async Task<int> RunAsync(string root, CancellationToken ct = default)
	{
		if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("INSTANCE_ID") != "staging" ||
			Environment.GetEnvironmentVariable("SHOP_ALLOW_STAGING_HISTORY_TELEGRAM_SEND") != "1" ||
			!Path.IsPathFullyQualified(root) || !root.StartsWith("/app/data/teleg-staging-history-", StringComparison.Ordinal) ||
			Path.GetFullPath(root) != root || root.Length > 200 || !Directory.Exists(root) ||
			new DirectoryInfo(root).LinkTarget is not null || (File.GetUnixFileMode(root) & (UnixFileMode)63) != 0)
			throw new UnauthorizedAccessException();
		using var config = JsonDocument.Parse(ReadPrivate(Path.Combine(root, "runtime-binding.json"), 4096));
		var binding = config.RootElement;
		if (binding.ValueKind != JsonValueKind.Object || binding.EnumerateObject().Count() != 8 ||
			binding.GetProperty("schemaVersion").GetInt32() != 1 ||
			binding.GetProperty("instanceId").GetString() != "staging" ||
			binding.GetProperty("botUsername").GetString() != "testadbadbot")
			throw new UnauthorizedAccessException();
		var brand = binding.GetProperty("brandId").GetGuid();
		var deployment = binding.GetProperty("deploymentId").GetGuid();
		long revision = binding.GetProperty("revision").GetInt64();
		long bot = binding.GetProperty("botId").GetInt64();
		long actor = binding.GetProperty("actorId").GetInt64();
		if (brand == Guid.Empty || deployment == Guid.Empty || revision <= 0 || bot <= 0 || actor <= 0)
			throw new UnauthorizedAccessException();
		var registry = new BotBrandRegistry([new("staging-history", "verified", brand, bot, deployment, revision, true)]);
		var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "verified")], "staging-history"));
		if (!registry.TryResolveAuthenticated("staging-history", principal, out var context))
			throw new UnauthorizedAccessException();
		string token = ReadText(Path.Combine(root, "bot-token"), 128);
		string header = ReadText(Path.Combine(root, "webhook-header"), 43);
		byte[] request = ReadPrivate(Path.Combine(root, "telegram-update.json"), 1048576);
		byte[] secret = ReadPrivate(Path.Combine(root, "webhook-secret"), 32);
		if (secret.Length != 32) throw new UnauthorizedAccessException();
		using var verifier = new TelegramWebhookVerifier(context!, secret);
		CryptographicOperations.ZeroMemory(secret);
		string connectionString = ReadText(Path.Combine(root, "database-connection"), 1024);
		var connection = new NpgsqlConnectionStringBuilder(connectionString);
		if (connection.Host != "127.0.0.1" || connection.Database is not string database || !database.StartsWith("shop_staging_history_", StringComparison.Ordinal) ||
			connection.Username != "shop_staging_history_reader" || connection.IncludeErrorDetail || connection.Multiplexing)
			throw new UnauthorizedAccessException();
		await using var data = NpgsqlDataSource.Create(connection.ConnectionString);
		using var secrets = SecretFiles.Load(root, "service-config.json",
			new SecretLoadGrant(SecretService.BotHost, deployment, revision, [brand], false));
		var files = new CustomerHistoryFiles(Path.Combine(root, "teleg-customer-history-reviewed"), secrets);
		var router = new CustomerHistoryCommandRouter(new DbSessionFactory(data), new CustomerHistoryArchive(secrets), files);
		using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
		var pipeline = new CustomerHistoryDeliveryPipeline(router, new CustomerHistoryTelegramSender(http));
		var runtime = new CustomerHistoryPrivateRuntime(verifier, pipeline, token, actor,
			Path.Combine(root, "teleg-history-attempts"));
		var result = await runtime.HandleAsync([header], request, ct);
		if (!result.Completed) throw new UnauthorizedAccessException();
		Console.WriteLine("PASS: isolated authenticated private historical response confirmed; no commerce actions.");
		return 0;
	}
}
