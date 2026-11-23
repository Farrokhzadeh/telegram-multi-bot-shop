using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Npgsql;
using Platform.Persistence;
using Platform.Security;
using Platform.Telegram;

namespace Platform.Api;

internal static class SyntheticStaging
{
    private const string Root = "/run/staging";
    private const long Tester = 7001;
    private const long Staff = -10088;
    private static readonly string[] BotRoles = ["bot-a", "bot-b", "bot-c"];
    private static readonly Guid[] Brands = [
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
        Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc")];

    internal static async Task<int> RunAsync(string role)
    {
        try
        {
            if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("SHOP_STAGING_MODE") != "isolated-synthetic-v1" ||
                !Guid.TryParseExact(Environment.GetEnvironmentVariable("SHOP_STAGING_DEPLOYMENT"), "D", out var deployment) ||
                deployment == Guid.Empty) return 78;

            int bot = Array.IndexOf(BotRoles, role);
            SecretService service = role switch
            {
                "api" => SecretService.Api,
                "jobs" => SecretService.JobHost,
                _ when bot >= 0 => SecretService.BotHost,
                _ => throw new InvalidOperationException()
            };
            var grant = new SecretLoadGrant(service, deployment, 1, bot >= 0 ? [Brands[bot]] : Brands, role == "jobs");
            using var keys = SecretFiles.Load(Path.Combine(Root, "keys"), "service-config.json", grant);
            var options = new NpgsqlConnectionStringBuilder
            {
                Host = "stage-db",
                Database = "shop_staging",
                Username = "shop_staging_" + role.Replace('-', '_'),
                Password = File.ReadAllText(Path.Combine(Root, "db-password")),
                Timeout = 3,
                CommandTimeout = 5,
                MaxPoolSize = 2
            };
            await using var data = new NpgsqlSlimDataSourceBuilder(options.ConnectionString).EnableArrays().Build();
            await using (var connection = await data.OpenConnectionAsync())
            await using (var query = new NpgsqlCommand("""
                SELECT current_database()='shop_staging' AND current_user=$1
                    AND current_setting('server_version_num')='180006'
                    AND NOT has_schema_privilege(current_user,'shop','CREATE')
                    AND obj_description('shop'::regnamespace,'pg_namespace')=$2
                """, connection))
            {
                query.Parameters.Add(new() { Value = options.Username });
                query.Parameters.Add(new() { Value = "shop-staging-synthetic:" + deployment.ToString("D") + ":" + SchemaRunner.BaselineSqlSha256 });
                if (await query.ExecuteScalarAsync() is not true) return 78;
            }

            using var certificate = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(Root, "tls.pfx"), "", X509KeyStorageFlags.EphemeralKeySet);
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrelHttpsConfiguration().ConfigureKestrel(server =>
            {
                server.Limits.MaxRequestBodySize = 4096;
                server.ListenAnyIP(8443, endpoint => endpoint.UseHttps(certificate));
            });
            await using var app = builder.Build();
            var sessions = new DbSessionFactory(data);
            app.UseRouting();
            app.UseMiddleware<DiagnosticsMiddleware>((Action<string>)(_ => { }));
            app.MapGet("/ready", () => Results.Text(role));
            if (role == "api")
            {
                app.UseMiddleware<AdminGuardMiddleware>((Func<AdminToken, CancellationToken, Task<AuthenticatedAdmin?>>)(async (token, ct) =>
                {
                    await using var session = await sessions.BeginReadOnlyAsync(ct);
                    return await AdminSessionStore.AuthenticateAsync(session, token, ct);
                }));
                app.MapGet("/v1/admin/staging", () => Results.Text("synthetic-stage-admin")).RequireAdministrator();
            }
            else if (bot >= 0)
            {
                var registry = new BotBrandRegistry([new("staging", role, Brands[bot], 1001 + bot, deployment, 1, true)]);
                var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, role)], "staging"));
                if (!registry.TryResolveAuthenticated("staging", principal, out var context)) return 78;
                using var webhook = new TelegramWebhookVerifier(context!, File.ReadAllBytes(Path.Combine(Root, "webhook")));
                var router = new UpdateRouter(sessions.ForBrandWriter(context!, 0), (_, _, _, _) => throw new TelegramIngressRejectedException());
                app.MapPost("/callbacks/stage-" + role[^1], async Task<IResult> (HttpRequest request, CancellationToken ct) =>
                {
                    if (request.ContentLength is not (>= 2 and <= 4096)) return Results.BadRequest();
                    byte[] body = new byte[(int)request.ContentLength.Value];
                    await request.Body.ReadExactlyAsync(body, ct);
                    try
                    {
                        var update = webhook.Verify(request.Headers["X-Telegram-Bot-Api-Secret-Token"].Select(value => value ?? "").ToArray(), body);
                        if (update.ActorId != Tester || !update.IsCallback) return Results.StatusCode(403);
                        return Results.Text((await router.RouteAsync(update, ct)).Code.ToString());
                    }
                    catch (TelegramIngressRejectedException) { return Results.Unauthorized(); }
                });
                await app.RunAsync();
                return 0;
            }
            else
            {
                byte[] probe = File.ReadAllBytes(Path.Combine(Root, "recipient-probe"));
                app.MapPost("/fake-delivery/{kind}/{chat:long}", (string kind, long chat, HttpRequest request) =>
                {
                    var header = request.Headers["X-Staging-Probe"];
                    if (header.Count != 1 || header[0] is not { Length: 64 } token ||
                        !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(token), probe)) return Results.Unauthorized();
                    return (kind, chat) is ("customer_dm", Tester) or ("staff_chat", Staff)
                        ? Results.NoContent() : Results.StatusCode(403);
                });
            }
            await app.RunAsync();
            return 0;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("Synthetic staging startup rejected.");
            return 78;
        }
    }
}
