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
    public async Task AdministratorBrowsesThenMapsOnlyOneChosenItemWithAuditedReplay()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        var actor = await f.AdminAsync(Ct);
        await Mapping(f, actor);
        using var files = new SyntheticSecretFiles();
        using var credentials = Credentials(files);
        int lists = 0, details = 0;
        using var inspection = new AkundingSourceInspection(f.Sessions, f.Secrets, credentials, "public account", new Handler(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            if (request.RequestUri!.AbsolutePath == "/api/v1/products")
            {
                lists++; Assert.Equal("?include_out_of_stock=true", request.RequestUri.Query);
                return new(HttpStatusCode.OK) { Content = new StringContent("[" + """{"id":9,"name":"Chosen public code","unit_label":"code","your_price":2,"stock":0,"available":false}""" + "]", Encoding.UTF8, "application/json") };
            }
            details++; Assert.Equal("/api/v1/products/9", request.RequestUri.AbsolutePath);
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"id":9,"name":"Chosen public code","unit_label":"code","your_price":2,"stock":0,"available":false}""", Encoding.UTF8, "application/json") };
        }));
        long revision = await f.CountAsync("SELECT revision FROM shop.products", Ct);
        var page = await inspection.BrowseAsync(actor, f.Product, revision, "code", null, 10, Ct);
        Assert.Equal(9, Assert.Single(page.Items).Id);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.product_supplier_sources", Ct));
        Guid source = Guid.NewGuid(), command = Guid.NewGuid();
        for (int i = 0; i < 2; i++)
        {
            var chosen = await inspection.AddCandidateAsync(actor, f.Product, revision, 9, source, command, Ct);
            Assert.Equal(source, chosen.SourceId); Assert.Equal(1, chosen.SourceRevision); Assert.False(chosen.Product.Available);
        }
        Assert.Equal(1, lists); Assert.Equal(2, details);
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.product_supplier_sources WHERE NOT enabled AND observation_revision=0", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.product_supplier_sources WHERE external_product_id='9'", Ct));
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.audit_events WHERE action='create' AND entity_kind='product_supplier_source'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.products WHERE active_supplier_source_id IS NOT NULL", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.jobs", Ct));
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BrowseAndChoiceRejectRevocationOrProductChangesDuringNetwork(bool choose, bool revoke)
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        var actor = await f.AdminAsync(Ct);
        await Mapping(f, actor);
        long revision = await f.CountAsync("SELECT revision FROM shop.products", Ct);
        using var files = new SyntheticSecretFiles();
        using var credentials = Credentials(files);
        using var inspection = new AkundingSourceInspection(f.Sessions, f.Secrets, credentials, "public account", new AsyncHandler(async (_, ct) =>
        {
            await using var edit = await f.Sessions.BeginAsync(ct);
            if (revoke) await AdminSessionStore.LogoutAsync(edit, actor, ct);
            else await edit.ExecuteAsync(new DbStatement("UPDATE shop.products SET revision=revision+1 WHERE id=$1", DbValue.Uuid(f.Product)), ct);
            await edit.CommitAsync(ct);
            if (choose) return Reply();
            return new(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") };
        }));
        async Task Read()
        {
            if (choose) await inspection.AddCandidateAsync(actor, f.Product, revision, 7, Guid.NewGuid(), Guid.NewGuid(), Ct);
            else await inspection.BrowseAsync(actor, f.Product, revision, "", null, 10, Ct);
        }
        if (revoke) await Assert.ThrowsAsync<AdminAuthenticationException>(Read);
        else await Assert.ThrowsAsync<InvalidOperationException>(Read);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.product_supplier_sources", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.jobs", Ct));
    }
    [Fact]
    public async Task RevisionCheckedMappingCreationRejectsStaleAndChangedReplayInputsAtomically()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        var actor = await f.AdminAsync(Ct);
        await Mapping(f, actor);
        long revision = await f.CountAsync("SELECT revision FROM shop.products", Ct);
        var store = new SupplierSourceSelectionStore(f.Sessions);
        Guid source = Guid.NewGuid(), command = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAtRevisionAsync(actor, f.Product, source, "akunding", "9", null, command, revision + 1, Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.audit_events WHERE action='create' AND entity_kind='product_supplier_source'", Ct));
        await store.CreateAtRevisionAsync(actor, f.Product, source, "akunding", "9", null, command, revision, Ct);
        await Assert.ThrowsAsync<DbOperationException>(() => store.CreateAtRevisionAsync(actor, f.Product, source, "akunding", "10", null, command, revision, Ct));
        await Assert.ThrowsAsync<DbOperationException>(() => store.CreateAtRevisionAsync(actor, f.Product, source, "akunding", "9", null, command, revision + 1, Ct));
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.product_supplier_sources", Ct));
    }
    [Fact]
    public async Task StaleProductAndRevokedAdministratorFailBeforeCatalogAccess()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        var actor = await f.AdminAsync(Ct);
        await Mapping(f, actor);
        long revision = await f.CountAsync("SELECT revision FROM shop.products", Ct);
        using var files = new SyntheticSecretFiles();
        using var credentials = Credentials(files);
        int calls = 0;
        using var inspection = new AkundingSourceInspection(f.Sessions, f.Secrets, credentials, "public account", new Handler(_ => { calls++; return Reply(); }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => inspection.BrowseAsync(actor, f.Product, revision + 1, "", null, 10, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => inspection.AddCandidateAsync(actor, f.Product, revision + 1, 7, Guid.NewGuid(), Guid.NewGuid(), Ct));
        await using (var logout = await f.Sessions.BeginAsync(Ct))
        {
            await AdminSessionStore.LogoutAsync(logout, actor, Ct); await logout.CommitAsync(Ct);
        }
        await Assert.ThrowsAsync<AdminAuthenticationException>(() => inspection.BrowseAsync(actor, f.Product, revision, "", null, 10, Ct));
        await Assert.ThrowsAsync<AdminAuthenticationException>(() => inspection.AddCandidateAsync(actor, f.Product, revision, 7, Guid.NewGuid(), Guid.NewGuid(), Ct));
        Assert.Equal(0, calls);
    }
    [Fact]
    public async Task MismatchedDetailNeverCreatesAChosenMapping()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        var actor = await f.AdminAsync(Ct);
        await Mapping(f, actor);
        using var files = new SyntheticSecretFiles();
        using var credentials = Credentials(files);
        using var inspection = new AkundingSourceInspection(f.Sessions, f.Secrets, credentials, "public account", new Handler(_ => Reply()));
        long revision = await f.CountAsync("SELECT revision FROM shop.products", Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => inspection.AddCandidateAsync(actor, f.Product, revision, 9, Guid.NewGuid(), Guid.NewGuid(), Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.product_supplier_sources", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.audit_events WHERE action='create' AND entity_kind='product_supplier_source'", Ct));
    }
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
    internal static SupplierCredentialSnapshot Credentials(SyntheticSecretFiles files, Guid? deployment = null, SecretService service = SecretService.JobHost, string provider = "akunding")
    {
        byte[] token = Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("N"));
        files.Write("public-supplier-token", token);
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 1); writer.WriteString("deploymentId", deployment ?? SyntheticSecretFiles.Deployment);
            writer.WriteNumber("revision", 7); writer.WriteString("service", service.ToString()); writer.WriteStartArray("accounts"); writer.WriteStartObject();
            writer.WriteString("provider", provider); writer.WriteString("accountRef", "public account"); writer.WriteString("credentialFile", "public-supplier-token");
            writer.WriteString("credentialSha256", Convert.ToHexString(SHA256.HashData(token))); writer.WriteEndObject(); writer.WriteEndArray(); writer.WriteEndObject();
        }
        CryptographicOperations.ZeroMemory(token);
        files.Write("supplier-config.json", bytes.ToArray());
        return SupplierCredentialFiles.Load(files.Root, "supplier-config.json", new(service, deployment ?? SyntheticSecretFiles.Deployment, 7, [new(provider, "public account")]));
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
