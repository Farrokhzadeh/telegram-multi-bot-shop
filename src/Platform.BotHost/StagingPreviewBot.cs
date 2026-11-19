using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Platform.Telegram;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Exceptions;

namespace Platform.BotHost;

internal static class StagingPreviewBot
{
    private const string TestBot = "testadbadbot";
    private const string Database = "akanti_staging_preview";
    private const string Reader = "akanti_preview_reader";
    private const string DatabaseMarker = "akanti-staging-preview:961b193ffa73d2a02ce66ad964361553ac366362fdeaf4a09cdaf8508e53470a";

    internal static async Task<int> RunAsync(string root)
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("INSTANCE_ID") != "staging" ||
            root != "/run/akanti-preview" || !Directory.Exists(root) || new DirectoryInfo(root).LinkTarget is not null ||
            (File.GetUnixFileMode(root) & (UnixFileMode)63) != 0) return 78;
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, args) => { args.Cancel = true; shutdown.Cancel(); };
        var ct = shutdown.Token;
        try
        {
            await ListenAsync(root, ct);
            return 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return 0; }
        catch (Exception)
        {
            Console.Error.WriteLine("Akanti staging preview stopped. Private inputs and Telegram responses were withheld.");
            return 78;
        }
    }

    private static byte[] ReadPrivate(string path, int maximum)
    {
        var file = new FileInfo(path);
        if (!OperatingSystem.IsLinux() || !file.Exists || file.LinkTarget is not null || file.Length < 1 || file.Length > maximum ||
            (File.GetUnixFileMode(path) & (UnixFileMode)63) != 0) throw new UnauthorizedAccessException();
        return File.ReadAllBytes(path);
    }

    private static long ReadNumber(string path) =>
        long.Parse(Encoding.ASCII.GetString(ReadPrivate(path, 24)).Trim(), NumberStyles.None, CultureInfo.InvariantCulture);

    private static void WritePrivate(string path, ReadOnlySpan<byte> bytes, bool replace = false)
    {
        if (!OperatingSystem.IsLinux()) throw new UnauthorizedAccessException();
        string temp = path + "." + Guid.NewGuid().ToString("N");
        using (var file = new FileStream(temp, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        }))
        {
            file.Write(bytes);
            file.Flush(true);
        }
        File.Move(temp, path, replace);
    }

    private static async Task ListenAsync(string root, CancellationToken ct)
    {
        var state = Path.Combine(root, "state");
        if (!OperatingSystem.IsLinux() || !Directory.Exists(state) || new DirectoryInfo(state).LinkTarget is not null ||
            (File.GetUnixFileMode(state) & (UnixFileMode)63) != 0) throw new UnauthorizedAccessException();
        await using var owner = new FileStream(Path.Combine(state, "listener.lock"), new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
        var ready = Path.Combine(state, "ready");
        File.Delete(ready);
        try
        {
            string token = Encoding.ASCII.GetString(ReadPrivate(Path.Combine(root, "bot-token"), 128)).Trim();
            long tester = ReadNumber(Path.Combine(root, "tester-id"));
            long expectedBot = ReadNumber(Path.Combine(root, "bot-id"));
            int colon = token.IndexOf(':');
            if (tester <= 0 || expectedBot <= 0 || colon <= 0 ||
                token[..colon] != expectedBot.ToString(CultureInfo.InvariantCulture) || token.Length < colon + 30 ||
                token[(colon + 1)..].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
                throw new UnauthorizedAccessException();
            var connection = new NpgsqlConnectionStringBuilder(Encoding.UTF8.GetString(ReadPrivate(Path.Combine(root, "database-connection"), 1024)));
            if (connection.Host != "127.0.0.1" || connection.Port != 5432 || connection.Database != Database ||
                connection.Username != Reader || connection.Multiplexing || connection.IncludeErrorDetail ||
                connection.Enlist || connection.LogParameters) throw new UnauthorizedAccessException();
            await using var database = new NpgsqlSlimDataSourceBuilder(connection.ConnectionString).Build();
            await CheckDatabaseAsync(database, ct);
            using var http = TelegramTransport.CreateHttpClient(TimeSpan.FromSeconds(38));
            var client = TelegramTransport.Create(token, http);
            var me = await client.GetMe(ct);
            if (me.Id != expectedBot || !me.IsBot || me.Username != TestBot) throw new UnauthorizedAccessException();
            var hook = await client.GetWebhookInfo(ct);
            if (hook.Url != "") throw new UnauthorizedAccessException();
            byte[] key = ReadPrivate(Path.Combine(root, "inbox-key"), 32);
            try
            {
                if (key.Length != 32) throw new UnauthorizedAccessException();
                using var inbox = new AesGcm(key, 16);
                await PollAsync(http, token, tester, database, inbox, state, ready, ct);
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        finally { File.Delete(ready); }
    }

    private static async Task CheckDatabaseAsync(NpgsqlDataSource database, CancellationToken ct)
    {
        await using var command = database.CreateCommand("""
            SELECT current_database() = 'akanti_staging_preview' AND current_user = 'akanti_preview_reader'
              AND current_setting('default_transaction_read_only') = 'on'
              AND shobj_description(d.oid, 'pg_database') = $1
              AND NOT r.rolsuper AND NOT r.rolcreatedb AND NOT r.rolcreaterole AND NOT r.rolbypassrls
            FROM pg_database d CROSS JOIN pg_roles r
            WHERE d.datname=current_database() AND r.rolname=current_user
            """);
        command.Parameters.AddWithValue(DatabaseMarker);
        if (await command.ExecuteScalarAsync(ct) is not true) throw new UnauthorizedAccessException();
    }

    private static async Task PollAsync(HttpClient http, string token, long tester, NpgsqlDataSource database, AesGcm inbox, string state, string ready, CancellationToken ct)
    {
        var cursor = Path.Combine(state, "next-offset");
        long next = File.Exists(cursor) ? ReadNumber(cursor) : 0;
        var sender = new CustomerHistoryTelegramSender(http);
        while (!ct.IsCancellationRequested)
        {
            JsonElement batch;
            try
            {
                batch = await TelegramTransport.GetUpdatesAsync(token, http, checked((int)next), 12, 25,
                    [UpdateType.Message, UpdateType.CallbackQuery], ct);
            }
            catch (RequestException error) when (TelegramTransport.StatusCode(error) is null or 429 or >= 500)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                continue;
            }
            if (!File.Exists(ready))
            {
                WritePrivate(ready, "isolated-staging-preview"u8);
                Console.WriteLine("Akanti staging Telegram preview ready.");
            }
            foreach (var record in batch.EnumerateArray())
            {
                long id = record.GetProperty("update_id").GetInt64();
                if (id <= 0 || id >= long.MaxValue) throw new InvalidOperationException();
                if (id < next) continue;
                var captured = Path.Combine(state, id.ToString(CultureInfo.InvariantCulture) + ".capture");
                if (!File.Exists(captured))
                {
                    if (Directory.EnumerateFiles(state, "*.capture").Take(2048).Count() >= 2048) throw new InvalidOperationException();
                    byte[] body = Encoding.UTF8.GetBytes(record.GetRawText());
                    try
                    {
                        byte[] encoded = new byte[28 + body.Length];
                        RandomNumberGenerator.Fill(encoded.AsSpan(0, 12));
                        inbox.Encrypt(encoded.AsSpan(0, 12), body, encoded.AsSpan(28), encoded.AsSpan(12, 16));
                        WritePrivate(captured, encoded);
                    }
                    finally { CryptographicOperations.ZeroMemory(body); }
                }
                next = id + 1;
                WritePrivate(cursor, Encoding.ASCII.GetBytes(next.ToString(CultureInfo.InvariantCulture)), replace: true);
                if (!StagingPreviewCommand.Accepts(record, tester)) continue;
                await CheckDatabaseAsync(database, ct);
                var message = new CustomerHistoryMessage("Akanti (آکانتی) staging is online. This private preview uses the isolated staging database. Checkout and delivery are pending.");
                var outcome = await sender.SendAsync(token, tester, message, ct);
                Console.WriteLine(outcome.State == CustomerHistorySendState.Confirmed ? "Private staging reply confirmed." : "Private staging reply requires review.");
            }
        }
    }
}
