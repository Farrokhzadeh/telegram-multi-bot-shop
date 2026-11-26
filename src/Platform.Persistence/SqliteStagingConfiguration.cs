using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Platform.Security;

namespace Platform.Persistence;

public sealed class SqliteStagingConfiguration
{
    private const string Database = "/stage/sqlite/shop.db";
    private static readonly Guid StageBrand = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid StageDeployment = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    private SqliteStagingConfiguration(BrandContext context, long telegramBotId, long recipient)
    {
        Context = context;
        TelegramBotId = telegramBotId;
        Recipient = recipient;
        Sessions = SqliteSessionFactory.ForPlaintext(Database);
    }

    public BrandContext Context { get; }
    public SqliteSessionFactory Sessions { get; }
    public long TelegramBotId { get; }
    public long Recipient { get; }
    public long SyntheticActor => 7001;

    public static async Task<SqliteStagingConfiguration> LoadAsync(CancellationToken ct = default)
    {
        string? format = Environment.GetEnvironmentVariable("SHOP_SQLITE_STAGING");
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("INSTANCE_ID") != "staging" ||
            format != "synthetic-acc-abad-v2" ||
            Environment.GetEnvironmentVariable("SHOP_STAGING_SQLITE_PATH") != Database ||
            !File.Exists(Database) || new FileInfo(Database).LinkTarget is not null ||
            (File.GetUnixFileMode(Database) & (UnixFileMode)63) != 0)
            throw new UnauthorizedAccessException("The persistent SQLite staging database is unavailable.");

        if (Environment.GetEnvironmentVariable("SHOP_STAGING_SECRET_ROOT") is not null)
            throw new UnauthorizedAccessException("Plaintext SQLite staging cannot use a legacy key mount.");

        long botId = ReadNumber("/stage/tester/bot-id");
        long recipient = ReadNumber("/stage/tester/tester-id");
        var binding = new BotBrandBinding("sqlite-stage", "authenticated-runtime", StageBrand, 1001,
            StageDeployment, 7, true);
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, binding.Subject)], binding.Scheme);
        var registry = new BotBrandRegistry([binding]);
        if (!registry.TryResolveAuthenticated(binding.Scheme, new ClaimsPrincipal(identity), out var context) ||
            context is null) throw new UnauthorizedAccessException("Staging registration is unavailable.");
        var state = new SqliteStagingConfiguration(context, botId, recipient);
        bool valid = await state.Sessions.RunAsync(false, async (_, tx, token) =>
        {
            return await SqliteSessionFactory.StepEfAsync(tx, false, async (data, cancel) => await data.Data.Set<Entities.Brand>().AnyAsync(row => row.Id == StageBrand && row.BotId == 1001 && row.PolicyVersion >= 0 && row.Active, cancel) && await data.Data.Set<Entities.User>().AnyAsync(row => row.BrandId == StageBrand && row.TelegramId == 7001 && row.BlockedAt == null && row.BannedAt == null, cancel), token);
        }, ct);
        if (!valid) throw new UnauthorizedAccessException("SQLite staging brand fixture is unavailable.");
        return state;
    }

    public byte[] ReadApiKey()
    {
        byte[] source = ReadPrivate("/stage/tester/api-key", 128);
        try
        {
            if (source.Length != 64 || source.Any(c => !(c is >= (byte)'0' and <= (byte)'9' or
                    >= (byte)'a' and <= (byte)'f')))
                throw new UnauthorizedAccessException("Staging service key is invalid.");
            return Convert.FromHexString(Encoding.ASCII.GetString(source));
        }
        finally { CryptographicOperations.ZeroMemory(source); }
    }

    public string ReadTelegramToken()
    {
        byte[] bytes = ReadPrivate("/stage/tester/bot-token", 160);
        try
        {
            string token = new UTF8Encoding(false, true).GetString(bytes).Trim();
            int colon = token.IndexOf(':');
            if (colon <= 0 || token[..colon] != TelegramBotId.ToString(CultureInfo.InvariantCulture) ||
                token.Length - colon - 1 is < 30 or > 90 ||
                token[(colon + 1)..].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
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
                    CultureInfo.InvariantCulture, out long value) || value <= 0)
                throw new UnauthorizedAccessException("Staging identity is invalid.");
            return value;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static byte[] ReadPrivate(string path, int limit)
    {
        if (!OperatingSystem.IsLinux()) throw new UnauthorizedAccessException();
        var file = new FileInfo(path);
        if (!file.Exists || file.LinkTarget is not null || file.Length is < 1 or > 160 ||
            file.Length > limit || (File.GetUnixFileMode(path) & (UnixFileMode)63) != 0)
            throw new UnauthorizedAccessException("Private staging configuration is unavailable.");
        return File.ReadAllBytes(path);
    }
}
