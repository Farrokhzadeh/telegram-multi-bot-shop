using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api;
using Platform.Payments;
using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class AdministrationHttpTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task AuthenticatedAdministrationUsesFrozenBodiesScopedPostingsPaginationAndCorrelatedErrors()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, stock: 0, ct: Ct);
        using var bank = new BankDestinationService(f.Sessions, f.Secrets, new byte[32]);
        var payments = new PaymentSettlementFixture(f);
        await f.AdminAsync(Ct);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.UseRouting();
        app.UseMiddleware<DiagnosticsMiddleware>((Action<string>)(_ => { }));
        app.UseMiddleware<AdminGuardMiddleware>((Func<AdminToken, CancellationToken, Task<AuthenticatedAdmin?>>)(async (token, ct) =>
        { await using var session = await f.Sessions.BeginAsync(ct); return await AdminSessionStore.AuthenticateAsync(session, token, ct); }));
        BrandContext? Resolve(Guid brand) => f.Contexts.SingleOrDefault(c => c.BrandId == brand);
        app.MapCatalogAdministration(f.Sessions, new(f.Sessions, f.Secrets), Resolve);
        app.MapOrderAdministration(new(f.Sessions, f.Secrets), new(f.Sessions, f.Secrets), payments.Manual, bank, Resolve);
        app.MapAdministrationOperations(new(f.Sessions));
        await app.StartAsync(Ct);
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        IssuedAdminSession issued;
        await using (var login = await f.Sessions.BeginAsync(Ct))
        { issued = await AdminSessionStore.LoginAsync(login, "public-checkout-admin", new string('s', 20).AsMemory(), TimeSpan.FromHours(1), Ct); await login.CommitAsync(Ct); }
        using (issued)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued.Token.ExportForTransport());
            var command = new WalletCommand(f.Brands[0], new(f.Users[0], "5.25", 0, "Public HTTP adjustment", Guid.NewGuid()));
            string body = JsonSerializer.Serialize(command, OrderApiJsonContext.Default.WalletCommand);
            for (int i = 0; i < 2; i++)
            {
                using var response = await Post(body);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var identity = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(Ct), OrderApiJsonContext.Default.AdminIdentity);
                Assert.Equal(command.Value.CommandId, identity!.Id);
            }
            string first = await client.GetStringAsync("/v1/admin/brands?limit=1", Ct);
            var page = JsonSerializer.Deserialize(first, CatalogApiJsonContext.Default.AdminPageAdminBrand)!;
            Assert.Single(page.Items);
            Assert.NotNull(page.Next);
            var next = JsonSerializer.Deserialize(await client.GetStringAsync("/v1/admin/brands?limit=1&after=" + page.Next.Value.ToString("D"), Ct), CatalogApiJsonContext.Default.AdminPageAdminBrand)!;
            Assert.NotEqual(page.Items[0].Id, next.Items[0].Id);
            using (var invalid = await client.GetAsync("/v1/admin/inventory", Ct)) await Error(invalid, HttpStatusCode.BadRequest, "validation_failed");
            using (var invalid = await client.GetAsync("/v1/admin/products?limit=0", Ct)) await Error(invalid, HttpStatusCode.BadRequest, "validation_failed");
            using (var duplicate = await Post(body[..^1] + ",\"BrandId\":\"" + f.Brands[0].ToString("D") + "\"}")) await Error(duplicate, HttpStatusCode.BadRequest, "validation_failed");
            string wrong = JsonSerializer.Serialize(command with { BrandId = f.Brands[1], Value = command.Value with { CommandId = Guid.NewGuid() } }, OrderApiJsonContext.Default.WalletCommand);
            using (var foreign = await Post(wrong)) await Error(foreign, HttpStatusCode.NotFound, "not_found");
            Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", Ct));
            Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.wallets WHERE balance=0", Ct));
            await using (var revoke = await f.Sessions.BeginAsync(Ct))
            { await revoke.ExecuteAsync(new DbStatement("UPDATE shop.admin_sessions SET revoked_at=clock_timestamp() WHERE id=$1", DbValue.Uuid(issued.Id)), Ct); await revoke.CommitAsync(Ct); }
            using var denied = await Post(body);
            await Error(denied, HttpStatusCode.Unauthorized, "unauthenticated");
        }
        await app.StopAsync(Ct);
        Task<HttpResponseMessage> Post(string json) => client.PostAsync("/v1/admin/wallet-adjustments", new StringContent(json, Encoding.UTF8, "application/json"), Ct);
    }
    private static async Task Error(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
        Assert.Equal(response.Headers.GetValues("X-Request-Id").Single(), body.RootElement.GetProperty("requestId").GetString());
    }
}
