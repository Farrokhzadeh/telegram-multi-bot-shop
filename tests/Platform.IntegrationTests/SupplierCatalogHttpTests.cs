using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api;
using Platform.Fulfillment.Providers;
using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class SupplierCatalogHttpTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task OptionalRoutesRequireIndependentAuthenticationBeforeBindingOrSupplierAccess()
    {
        await using var f = await Fixture.CreateAsync();
        var endpoints = ((IEndpointRouteBuilder)f.App).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ToArray();
        Assert.Equal(4, endpoints.Length);
        Assert.All(endpoints, e => Assert.NotNull(e.Metadata.GetMetadata<AdministratorEndpointMetadata>()));
        f.Client.DefaultRequestHeaders.Add("X-Admin", "true");
        f.Client.DefaultRequestHeaders.Add("X-Qualification-Bot", "a");
        string[] paths = [f.Root + "/catalog", f.Sources, f.Root + "/sources", f.Root + "/sources/" + Guid.NewGuid().ToString("D")];
        for (int i = 0; i < paths.Length; i++)
        {
            using var request = new HttpRequestMessage(i == 2 ? HttpMethod.Post : HttpMethod.Get, paths[i]);
            if (i == 2) request.Content = new StringContent("not json", Encoding.UTF8, "application/json");
            using var response = await f.Client.SendAsync(request, Ct);
            await Error(response, HttpStatusCode.Unauthorized);
        }
        Assert.Equal(0, f.Calls);
        Assert.Equal(0L, await f.Data.CountAsync("SELECT count(*) FROM shop.product_supplier_sources", Ct));
        Assert.Equal(29, OpenApiContract.Routes.Length);
    }
    [Fact]
    public async Task AdministratorCanBrowseChooseReplayListAndPreviewWithoutEnablingOrBuying()
    {
        await using var f = await Fixture.CreateAsync();
        await f.LoginAsync();
        using (var first = await f.Client.GetAsync(f.Root + "/catalog?productRevision=2&limit=1", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.True(first.Headers.CacheControl!.NoStore);
            using var json = JsonDocument.Parse(await first.Content.ReadAsStringAsync(Ct));
            Assert.Equal(7, json.RootElement.GetProperty("Items")[0].GetProperty("Id").GetInt64());
            Assert.Equal(7, json.RootElement.GetProperty("Next").GetInt64());
            Assert.DoesNotContain("credential", json.RootElement.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        using (var second = await f.Client.GetAsync(f.Root + "/catalog?productRevision=2&after=7&search=code", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            using var json = JsonDocument.Parse(await second.Content.ReadAsStringAsync(Ct));
            Assert.Equal(9, json.RootElement.GetProperty("Items")[0].GetProperty("Id").GetInt64());
            Assert.False(json.RootElement.GetProperty("Items")[0].GetProperty("Available").GetBoolean());
        }
        Guid source = Guid.NewGuid(), command = Guid.NewGuid();
        string body = Choice(source, command);
        for (int i = 0; i < 2; i++)
        {
            using var saved = await f.PostAsync(body);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            Assert.True(saved.Headers.CacheControl!.NoStore);
            using var json = JsonDocument.Parse(await saved.Content.ReadAsStringAsync(Ct));
            Assert.Equal(source, json.RootElement.GetProperty("SourceId").GetGuid());
            Assert.Equal("USDT", json.RootElement.GetProperty("Product").GetProperty("Currency").GetString());
        }
        using (var metadata = await f.Client.GetAsync(f.Sources, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
            using var json = JsonDocument.Parse(await metadata.Content.ReadAsStringAsync(Ct));
            var mapping = Assert.Single(json.RootElement.GetProperty("Items").EnumerateArray());
            Assert.Equal(source, mapping.GetProperty("Id").GetGuid());
            Assert.False(mapping.GetProperty("Enabled").GetBoolean());
            Assert.False(mapping.GetProperty("Selected").GetBoolean());
        }
        using (var preview = await f.Client.GetAsync(f.Root + "/sources/" + source.ToString("D") + "?sourceRevision=1", Ct))
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        using (var stale = await f.Client.GetAsync(f.Root + "/catalog?productRevision=1", Ct))
            await Error(stale, HttpStatusCode.Conflict);
        using (var conflict = await f.PostAsync(Choice(source, command, 9)))
            await Error(conflict, HttpStatusCode.Conflict);
        int calls = f.Calls;
        string[] invalid = ["{}", body[..^1] + ",\"CommandId\":\"" + command.ToString("D") + "\"}", body[..^1] + ",\"ApiKey\":\"public-do-not-echo\"}", body.Replace("\"ProductRevision\":2", "\"ProductRevision\":0", StringComparison.Ordinal)];
        foreach (string input in invalid)
        {
            using var rejected = await f.PostAsync(input);
            await Error(rejected, HttpStatusCode.BadRequest);
        }
        using (var invalid = await f.Client.GetAsync(f.Root + "/catalog?productRevision=2&limit=101", Ct))
            await Error(invalid, HttpStatusCode.BadRequest);
        using (var missing = await f.Client.GetAsync(f.Root + "/catalog", Ct))
            await Error(missing, HttpStatusCode.BadRequest);
        using (var wrongType = await f.Client.PostAsync(f.Root + "/sources", new StringContent(body, Encoding.UTF8, "text/plain"), Ct))
            await Error(wrongType, HttpStatusCode.UnsupportedMediaType);
        Assert.Equal(calls, f.Calls);
        Assert.Equal(1L, await f.Data.CountAsync("SELECT count(*) FROM shop.product_supplier_sources WHERE NOT enabled AND revision=1 AND observation_revision=0", Ct));
        Assert.Equal(1L, await f.Data.CountAsync("SELECT count(*) FROM shop.audit_events WHERE action='create' AND entity_kind='product_supplier_source'", Ct));
        Assert.Equal(0L, await f.Data.CountAsync("SELECT count(*) FROM shop.orders", Ct));
        Assert.Equal(0L, await f.Data.CountAsync("SELECT count(*) FROM shop.jobs", Ct));
        await using (var revoke = await f.Data.Sessions.BeginAsync(Ct))
        {
            await AdminSessionStore.LogoutAsync(revoke, f.Actor!, Ct); await revoke.CommitAsync(Ct);
        }
        using var denied = await f.Client.GetAsync(f.Root + "/catalog?productRevision=2", Ct);
        await Error(denied, HttpStatusCode.Unauthorized);
        Assert.Equal(calls, f.Calls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthorityOrProductChangesDuringHttpSelectionNeverSaveMapping(bool revoke)
    {
        await using var f = await Fixture.CreateAsync();
        await f.LoginAsync();
        f.OnRequest = async ct =>
        {
            await using var edit = await f.Data.Sessions.BeginAsync(ct);
            if (revoke) await AdminSessionStore.LogoutAsync(edit, f.Actor!, ct);
            else await edit.ExecuteAsync(new DbStatement("UPDATE shop.products SET revision=revision+1 WHERE id=$1", DbValue.Uuid(f.Data.Product)), ct);
            await edit.CommitAsync(ct);
        };
        using var response = await f.PostAsync(Choice(Guid.NewGuid(), Guid.NewGuid()));
        await Error(response, revoke ? HttpStatusCode.Unauthorized : HttpStatusCode.Conflict);
        Assert.Equal(1, f.Calls);
        Assert.Equal(0L, await f.Data.CountAsync("SELECT count(*) FROM shop.product_supplier_sources", Ct));
        Assert.Equal(0L, await f.Data.CountAsync("SELECT count(*) FROM shop.audit_events WHERE action='create' AND entity_kind='product_supplier_source'", Ct));
    }
    [Fact]
    public async Task SupplierFailureReturnsOnlyCorrelatedSafeErrorWithoutCreatingMapping()
    {
        await using var f = await Fixture.CreateAsync();
        await f.LoginAsync();
        f.Fail = true;
        using var response = await f.PostAsync(Choice(Guid.NewGuid(), Guid.NewGuid()));
        await Error(response, HttpStatusCode.Conflict);
        Assert.Equal(1, f.Calls);
        Assert.Equal(0L, await f.Data.CountAsync("SELECT count(*) FROM shop.product_supplier_sources", Ct));
    }
    private static string Choice(Guid source, Guid command, long external = 7) => "{\"ProductRevision\":2,\"ExternalProductId\":" + external + ",\"SourceId\":\"" + source.ToString("D") + "\",\"CommandId\":\"" + command.ToString("D") + "\"}";
    private static async Task Error(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        string text = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("public-do-not-echo", text, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(text);
        Assert.Equal(2, json.RootElement.EnumerateObject().Count());
        Assert.Equal(response.Headers.GetValues("X-Request-Id").Single(), json.RootElement.GetProperty("requestId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("code").GetString()));
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public CheckoutFixture Data { get; }
        public WebApplication App { get; }
        public HttpClient Client { get; }
        public AuthenticatedAdmin? Actor { get; private set; }
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public Func<CancellationToken, Task>? OnRequest { get; set; }
        public string Root => "/v1/admin/products/" + Data.Product.ToString("D") + "/suppliers/akunding";
        public string Sources => "/v1/admin/products/" + Data.Product.ToString("D") + "/supplier-sources";
        private readonly SyntheticSecretFiles files;
        private readonly SupplierCredentialSnapshot credentials;
        private readonly AkundingSourceInspection inspection;
        private Fixture(CheckoutFixture data, WebApplication app, SyntheticSecretFiles files, SupplierCredentialSnapshot credentials, AkundingSourceInspection inspection)
        {
            Data = data; App = app; this.files = files; this.credentials = credentials; this.inspection = inspection;
            Client = new() { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        }
        public static async Task<Fixture> CreateAsync()
        {
            var data = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
            await using (var edit = await data.Sessions.BeginAsync(Ct))
            {
                await edit.ExecuteAsync(new DbStatement("UPDATE shop.products SET fulfillment_kind='supplier',revision=revision+1 WHERE id=$1", DbValue.Uuid(data.Product)), Ct); await edit.CommitAsync(Ct);
            }
            var files = new SyntheticSecretFiles();
            var credentials = AkundingSourceInspectionTests.Credentials(files);
            Fixture? fixture = null;
            var inspection = new AkundingSourceInspection(data.Sessions, data.Secrets, credentials, "public account", new Handler(async (request, ct) =>
            {
                var current = fixture ?? throw new InvalidOperationException("HTTP fixture is unavailable.");
                current.Calls++;
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("akunding.shop", request.RequestUri!.Host);
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                if (current.OnRequest is { } callback) await callback(ct);
                if (current.Fail) return new(HttpStatusCode.InternalServerError) { Content = new StringContent("public-do-not-echo") };
                const string first = """{"id":7,"name":"Public test account","unit_label":"account","your_price":1.25,"stock":2,"available":true}""";
                const string second = """{"id":9,"name":"Public test code","unit_label":"code","your_price":2,"stock":0,"available":false}""";
                return new(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri.AbsolutePath.EndsWith("/products", StringComparison.Ordinal) ? "[" + first + "," + second + "]" : request.RequestUri.AbsolutePath.EndsWith("/9", StringComparison.Ordinal) ? second : first, Encoding.UTF8, "application/json") };
            }));
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            app.UseRouting();
            app.UseMiddleware<DiagnosticsMiddleware>((Action<string>)(_ => { }));
            app.UseMiddleware<AdminGuardMiddleware>((Func<AdminToken, CancellationToken, Task<AuthenticatedAdmin?>>)(async (token, ct) =>
            {
                await using var session = await data.Sessions.BeginAsync(ct);
                return await AdminSessionStore.AuthenticateAsync(session, token, ct);
            }));
            app.MapAkundingSupplierAdministration(inspection, new(data.Sessions));
            await app.StartAsync(Ct);
            fixture = new(data, app, files, credentials, inspection);
            return fixture;
        }
        public async Task LoginAsync()
        {
            await using var login = await Data.Sessions.BeginAsync(Ct);
            using var issued = await AdminSessionStore.LoginAsync(login, "public-checkout-admin", new string('s', 20).AsMemory(), TimeSpan.FromHours(1), Ct);
            await login.CommitAsync(Ct);
            await using var resolve = await Data.Sessions.BeginAsync(Ct);
            Actor = await AdminSessionStore.AuthenticateAsync(resolve, issued.Token, Ct);
            Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued.Token.ExportForTransport());
        }
        public Task<HttpResponseMessage> PostAsync(string body) => Client.PostAsync(Root + "/sources", new StringContent(body, Encoding.UTF8, "application/json"), Ct);
        public async ValueTask DisposeAsync()
        {
            Client.Dispose(); await App.StopAsync(Ct); await App.DisposeAsync(); inspection.Dispose(); credentials.Dispose(); files.Dispose(); await Data.DisposeAsync();
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request, ct);
    }
}
