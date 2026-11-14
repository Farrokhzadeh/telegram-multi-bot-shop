using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api;
using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.ContractTests;

public sealed class ScopedAdministrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task HttpCompositionUsesBoundBrandAndSharedWritersAcrossPause()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        var actor = await f.AdminAsync(Ct);
        var writers = new WriterRegistry(f.Sessions, f.Contexts.Select(c => new BrandWriterRegistration(c, 0)));
        var services = f.Contexts.ToDictionary(c => c.BrandId, c => new BrandAdministrationServices(writers, c, f.Secrets,
            new CatalogStore(writers.ForBrand(c), f.Secrets, [], (_, _) => throw new InvalidOperationException()), new byte[32]));
        try
        {
            var builder = WebApplication.CreateSlimBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var app = builder.Build(); app.UseRouting();
            app.UseMiddleware<DiagnosticsMiddleware>((Action<string>)(_ => { }));
            app.UseMiddleware<AdminGuardMiddleware>((Func<AdminToken, CancellationToken, Task<AuthenticatedAdmin?>>)(async (token, ct) =>
            { await using var session = await f.Sessions.BeginAsync(ct); return await AdminSessionStore.AuthenticateAsync(session, token, ct); }));
            app.MapScopedCatalogAdministration(writers, f.Secrets);
            app.MapScopedOrderAdministration(writers, f.Secrets, c => services[c.BrandId]);
            await app.StartAsync(Ct);
            using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
            await using var login = await f.Sessions.BeginAsync(Ct);
            using var ticket = await AdminSessionStore.LoginAsync(login, "public-checkout-admin", new string('s', 20).AsMemory(), TimeSpan.FromHours(1), Ct);
            await login.CommitAsync(Ct); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ticket.Token.ExportForTransport());
            async Task<HttpStatusCode> Adjust(int brand, long version)
            {
                string body = "{\"BrandId\":\"" + f.Brands[brand] + "\",\"Value\":{\"MemberId\":\"" + f.Users[brand] + "\",\"AmountUsd\":\"1\",\"ExpectedVersion\":" + version + ",\"Reason\":\"public synthetic adjustment\",\"CommandId\":\"" + Guid.NewGuid() + "\"}}";
                using var response = await client.PostAsync("/v1/admin/wallet-adjustments", new StringContent(body, Encoding.UTF8, "application/json"), Ct);
                return response.StatusCode;
            }
            async Task<HttpStatusCode> Import()
            {
                string body = "{\"ProductId\":\"" + f.Product + "\",\"Items\":[{\"Id\":\"" + Guid.NewGuid() + "\",\"Credential\":\"public synthetic " + Guid.NewGuid() + "\"}],\"CommandId\":\"" + Guid.NewGuid() + "\"}";
                using var response = await client.PostAsync("/v1/admin/inventory/import", new StringContent(body, Encoding.UTF8, "application/json"), Ct);
                return response.StatusCode;
            }
            Assert.Equal(HttpStatusCode.OK, await Adjust(0, 1));
            Assert.Equal(HttpStatusCode.NoContent, await Import());
            await writers.Controls.SetActiveAsync(actor, f.Context(0), 0, false, Guid.NewGuid(), Ct);
            Assert.True((int)await Adjust(0, 2) >= 400);
            Assert.True((int)await Import() >= 400);
            Assert.Equal(HttpStatusCode.OK, await Adjust(1, 1));
            Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.wallets WHERE balance=101 AND version=2", Ct));
            Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.inventory_items", Ct));
        }
        finally { foreach (var service in services.Values) service.Dispose(); }
    }
}
