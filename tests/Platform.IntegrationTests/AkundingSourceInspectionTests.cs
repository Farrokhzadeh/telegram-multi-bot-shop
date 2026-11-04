using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Platform.Fulfillment.Providers;
using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class AkundingSourceInspectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task ProtectedAdminPreviewReadsOnlyTheChosenDisabledMappingWithoutPublishingOrBuying()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        var actor = await f.AdminAsync(Ct);
        Guid source = await Mapping(f, actor);
        using var files = new SyntheticSecretFiles();
        using var credentials = Credentials(files);
        int calls = 0;
        HttpRequestMessage? sent = null;
        using var inspection = new AkundingSourceInspection(f.Sessions, f.Secrets, credentials, "public account", new Handler(request =>
        {
            calls++; sent = request;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://akunding.shop/api/v1/products/7", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(32, request.Headers.Authorization?.Parameter?.Length);
            return Reply();
        }));
        var preview = await inspection.InspectAsync(actor, f.Product, source, 1, Ct);
        Assert.Equal(source, preview.SourceId);
        Assert.Equal(1, preview.SourceRevision);
        Assert.Equal("USDT", preview.Product.Currency);
        Assert.Equal(1.25m, preview.Product.UnitPrice);
        Assert.Equal(1, calls);
        Assert.Null(sent!.Headers.Authorization);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.product_supplier_sources WHERE provider_code='akunding' AND NOT enabled AND revision=1 AND observation_revision=0", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.jobs", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.audit_events WHERE action IN ('enable','select_source')", Ct));
    }
    [Theory]
    [InlineData("account")]
    [InlineData("deployment")]
    [InlineData("service")]
    public async Task WrongProtectedScopeFailsBeforeAnyTransport(string mismatch)
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        using var files = new SyntheticSecretFiles();
        using var credentials = Credentials(files, mismatch == "deployment" ? Guid.NewGuid() : SyntheticSecretFiles.Deployment, mismatch == "service" ? SecretService.Api : SecretService.JobHost);
        int calls = 0;
        using var handler = new Handler(_ => { calls++; return Reply(); });
        if (mismatch == "account")
            Assert.Throws<SecretConfigurationException>(() => new AkundingSourceInspection(f.Sessions, f.Secrets, credentials, "another account", handler));
        else
            Assert.Throws<UnauthorizedAccessException>(() => new AkundingSourceInspection(f.Sessions, f.Secrets, credentials, "public account", handler));
        Assert.Equal(0, calls);
    }
    [Theory]
    [InlineData("stale")]
    [InlineData("foreign")]
    [InlineData("variant")]
    [InlineData("revoked")]
    public async Task InvalidMappingOrAdministratorCannotReadSupplier(string invalid)
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        var actor = await f.AdminAsync(Ct);
        Guid source = await Mapping(f, actor, invalid == "variant" ? "unexpected variant" : null);
        using var files = new SyntheticSecretFiles();
        using var credentials = Credentials(files);
        int calls = 0;
        using var inspection = new AkundingSourceInspection(f.Sessions, f.Secrets, credentials, "public account", new Handler(_ => { calls++; return Reply(); }));
        if (invalid == "revoked")
        {
            await using var logout = await f.Sessions.BeginAsync(Ct);
            await AdminSessionStore.LogoutAsync(logout, actor, Ct); await logout.CommitAsync(Ct);
            await Assert.ThrowsAsync<AdminAuthenticationException>(() => inspection.InspectAsync(actor, f.Product, source, 1, Ct));
        }
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => inspection.InspectAsync(actor, invalid == "foreign" ? Guid.NewGuid() : f.Product, source, invalid == "stale" ? 2 : 1, Ct));
        Assert.Equal(0, calls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevocationOrProductChangeDuringTransportPreventsPublishingThePreview(bool revoke)
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        var actor = await f.AdminAsync(Ct);
        Guid source = await Mapping(f, actor);
        using var files = new SyntheticSecretFiles();
        using var credentials = Credentials(files);
        using var inspection = new AkundingSourceInspection(f.Sessions, f.Secrets, credentials, "public account", new AsyncHandler(async (_, ct) =>
        {
            await using var edit = await f.Sessions.BeginAsync(ct);
            if (revoke) await AdminSessionStore.LogoutAsync(edit, actor, ct);
            else await edit.ExecuteAsync(new DbStatement("UPDATE shop.products SET revision=revision+1 WHERE id=$1", DbValue.Uuid(f.Product)), ct);
            await edit.CommitAsync(ct); return Reply();
        }));
        if (revoke) await Assert.ThrowsAsync<AdminAuthenticationException>(() => inspection.InspectAsync(actor, f.Product, source, 1, Ct));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => inspection.InspectAsync(actor, f.Product, source, 1, Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.jobs", Ct));
    }
    private static async Task<Guid> Mapping(CheckoutFixture f, AuthenticatedAdmin actor, string? variant = null)
    {
        await using (var edit = await f.Sessions.BeginAsync(Ct))
        {
            await edit.ExecuteAsync(new DbStatement("UPDATE shop.products SET fulfillment_kind='supplier',revision=revision+1 WHERE id=$1", DbValue.Uuid(f.Product)), Ct);
            await edit.CommitAsync(Ct);
        }
        Guid source = Guid.NewGuid();
        await new SupplierSourceSelectionStore(f.Sessions).CreateAsync(actor, f.Product, source, "akunding", "7", variant, Guid.NewGuid(), Ct);
        return source;
    }
    private static SupplierCredentialSnapshot Credentials(SyntheticSecretFiles files, Guid? deployment = null, SecretService service = SecretService.JobHost)
    {
        byte[] token = Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("N"));
        files.Write("public-supplier-token", token);
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 1); writer.WriteString("deploymentId", deployment ?? SyntheticSecretFiles.Deployment);
            writer.WriteNumber("revision", 7); writer.WriteString("service", service.ToString()); writer.WriteStartArray("accounts"); writer.WriteStartObject();
            writer.WriteString("provider", "akunding"); writer.WriteString("accountRef", "public account"); writer.WriteString("credentialFile", "public-supplier-token");
            writer.WriteString("credentialSha256", Convert.ToHexString(SHA256.HashData(token))); writer.WriteEndObject(); writer.WriteEndArray(); writer.WriteEndObject();
        }
        CryptographicOperations.ZeroMemory(token);
        files.Write("supplier-config.json", bytes.ToArray());
        return SupplierCredentialFiles.Load(files.Root, "supplier-config.json", new(service, deployment ?? SyntheticSecretFiles.Deployment, 7, [new("akunding", "public account")]));
    }
    private static HttpResponseMessage Reply() => new(HttpStatusCode.OK) { Content = new StringContent("""{"id":7,"name":"Public account","unit_label":"account","your_price":1.25,"stock":2,"available":true}""", Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
    }
    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request, ct);
    }
}
