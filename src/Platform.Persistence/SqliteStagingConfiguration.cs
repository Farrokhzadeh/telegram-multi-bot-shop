using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Platform.Security;

namespace Platform.Persistence;

public sealed class SqliteStagingConfiguration
{
    private const string Database = "/stage/sqlite/shop.db";
    private static readonly Guid StageBrand = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid StageDeployment = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    private SqliteStagingConfiguration(BrandContext context, long telegramBotId, long recipient,
        string secretRoot)
    {
        Context = context;
        TelegramBotId = telegramBotId;
        Recipient = recipient;
        SecretRoot = secretRoot;
        Sessions = new SqliteSessionFactory(Database);
    }

    public BrandContext Context { get; }
    public SqliteSessionFactory Sessions { get; }
    public long TelegramBotId { get; }
    public long Recipient { get; }
    public long SyntheticActor => 7001;
    public string SecretRoot { get; }

    public static async Task<SqliteStagingConfiguration> LoadAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("INSTANCE_ID") != "staging" ||
            Environment.GetEnvironmentVariable("SHOP_SQLITE_STAGING") != "synthetic-acc-abad-v1" ||
            Environment.GetEnvironmentVariable("SHOP_STAGING_SQLITE_PATH") != Database ||
            !File.Exists(Database) || new FileInfo(Database).LinkTarget is not null ||
            (File.GetUnixFileMode(Database) & (UnixFileMode)63) != 0)
            throw new UnauthorizedAccessException("The persistent SQLite staging database is unavailable.");

        string secretRoot = Environment.GetEnvironmentVariable("SHOP_STAGING_SECRET_ROOT") ?? "";
        if (!secretRoot.StartsWith("/stage/secrets/qualified-", StringComparison.Ordinal) ||
            !Directory.Exists(secretRoot) || new DirectoryInfo(secretRoot).LinkTarget is not null ||
            (File.GetUnixFileMode(secretRoot) & (UnixFileMode)63) != 0)
            throw new UnauthorizedAccessException("The staging secret scope is unavailable.");

        long botId = ReadNumber("/stage/tester/bot-id");
        long recipient = ReadNumber("/stage/tester/tester-id");
        var binding = new BotBrandBinding("sqlite-stage", "authenticated-runtime", StageBrand, 1001,
            StageDeployment, 7, true);
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, binding.Subject)], binding.Scheme);
        var registry = new BotBrandRegistry([binding]);
        if (!registry.TryResolveAuthenticated(binding.Scheme, new ClaimsPrincipal(identity), out var context) ||
            context is null) throw new UnauthorizedAccessException("Staging registration is unavailable.");
        var state = new SqliteStagingConfiguration(context, botId, recipient, secretRoot);
        bool valid = await state.Sessions.RunAsync(false, async (_, tx, token) =>
        {
            using var command = SqliteWallet.Command(tx, """
                SELECT (SELECT count(*) FROM brands WHERE active=1)=1
                    AND EXISTS(SELECT 1 FROM brands WHERE id=$brand AND bot_id=1001
                        AND policy_version>=0 AND active=1)
                    AND EXISTS(SELECT 1 FROM users WHERE brand_id=$brand AND telegram_id=7001
                        AND blocked_at IS NULL AND banned_at IS NULL)
                """, ("$brand", StageBrand));
            return (long)(await command.ExecuteScalarAsync(token))! == 1;
        }, ct);
        if (!valid) throw new UnauthorizedAccessException("SQLite staging brand fixture is unavailable.");
        return state;
    }

    public SecretSnapshot LoadSecrets(SecretService service)
    {
        if (service is not (SecretService.BotHost or SecretService.Api or SecretService.JobHost))
            throw new ArgumentOutOfRangeException(nameof(service));
        bool inventory = service == SecretService.JobHost;
        return SecretFiles.Load(SecretRoot, "service-config.json",
            new SecretLoadGrant(service, StageDeployment, 7, [StageBrand], inventory));
    }

    public string ReadTelegramToken()
    {
        byte[] bytes = ReadPrivate("/stage/tester/bot-token", 160);
        try
        {
            string token = new UTF8Encoding(false, true).GetString(bytes).Trim();
            int colon = token.IndexOf(':');
            if (colon<=0 || token[..colon] != TelegramBotId.ToString(CultureInfo.InvariantCulture) ||
                token.Length-colon-1 is < 30 or > 90 ||
                token[(colon+1)..].Any(c=>!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
                throw new UnauthorizedAccessException("Staging Telegram bot identity is unavailable.");
            return token;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static long ReadNumber(string path)
    {
        byte[] bytes = ReadPrivate(path, 32);
        try
        {
            if (!long.TryParse(Encoding.ASCII.GetString(bytes).Trim(), NumberStyles.None,
                    CultureInfo.InvariantCulture, out long value) || value<=0)
                throw new UnauthorizedAccessException("Staging identity is invalid.");
            return value;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static byte[] ReadPrivate(string path, int limit)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.LinkTarget is not null || file.Length is <1 or >160 ||
            file.Length>limit || (File.GetUnixFileMode(path) & (UnixFileMode)63)!=0)
            throw new UnauthorizedAccessException("Private staging configuration is unavailable.");
        return File.ReadAllBytes(path);
    }
}
