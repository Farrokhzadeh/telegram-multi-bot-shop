using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Platform.Persistence;
using Platform.Security;

namespace Platform.Fulfillment.Providers;

public sealed record AkundingSourcePreview(Guid SourceId, long SourceRevision, AkundingCatalogProduct Product);
public sealed class AkundingSourceInspection : IDisposable
{
    private readonly DbSessionFactory sessions;
    private readonly SupplierCredentialSnapshot credentials;
    private readonly string account;
    private readonly HttpClient http;
    private readonly AkundingCatalogClient catalog;
    public AkundingSourceInspection(DbSessionFactory sessions, SupplierCredentialGrant grant, SupplierCredentialSnapshot credentials,
        string account, HttpMessageHandler? transport = null)
        : this(sessions, credentials.ForGrant(grant), account, transport, grant.DeploymentId, grant.Service) { }

    private AkundingSourceInspection(DbSessionFactory sessions, SupplierCredentialSnapshot credentials,
        string account, HttpMessageHandler? transport, Guid deployment, SecretService service)
    {
        if (credentials.DeploymentId != deployment || credentials.Service != service)
            throw new UnauthorizedAccessException("Supplier credential scope is unavailable.");
        credentials.UseCredential("akunding", account, key => key.Length);
        this.sessions = sessions; this.credentials = credentials; this.account = account;
        http = new(new Authorization(credentials, account, transport ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }));
        catalog = new(http);
    }
    public async Task<AkundingSourcePreview> InspectAsync(AuthenticatedAdmin actor, Guid product, Guid source, long revision, CancellationToken ct)
    {
        if (product == Guid.Empty || source == Guid.Empty || revision <= 0) throw new ArgumentException("Invalid supplier inspection.");
        var before = await ReadAsync(actor, product, source, revision, ct);
        var result = await catalog.GetProductAsync(before.External, ct);
        var after = await ReadAsync(actor, product, source, revision, ct);
        if (before != after) throw new InvalidOperationException("Supplier mapping changed during inspection.");
        credentials.UseCredential("akunding", account, key => key.Length);
        return new(source, revision, result);
    }
    public async Task<AkundingCatalogPage> BrowseAsync(AuthenticatedAdmin actor, Guid product, long revision, string search, long? after, int limit, CancellationToken ct)
    {
        await RequireProductAsync(actor, product, revision, ct);
        var page = await catalog.ListProductsAsync(search, after, limit, ct);
        await RequireProductAsync(actor, product, revision, ct);
        return page;
    }
    public async Task<AkundingSourcePreview> AddCandidateAsync(AuthenticatedAdmin actor, Guid product, long revision, long externalProduct, Guid source, Guid command, CancellationToken ct)
    {
        if (source == Guid.Empty || command == Guid.Empty) throw new ArgumentException("Invalid supplier selection.");
        await RequireProductAsync(actor, product, revision, ct);
        var selected = await catalog.GetProductAsync(externalProduct, ct);
        await RequireProductAsync(actor, product, revision, ct);
        long created = await new SupplierSourceSelectionStore(sessions).CreateAtRevisionAsync(actor, product, source, "akunding", externalProduct.ToString(CultureInfo.InvariantCulture), null, command, revision, ct);
        return new(source, created, selected);
    }
    private async Task RequireProductAsync(AuthenticatedAdmin actor, Guid product, long revision, CancellationToken ct)
    {
        if (product == Guid.Empty || revision <= 0 || revision == long.MaxValue) throw new ArgumentException("Invalid supplier product.");
        credentials.UseCredential("akunding", account, key => key.Length);
        if (!await new SupplierInspectionStore(sessions).IsCurrentProductAsync(actor, product, revision, ct)) throw new InvalidOperationException("Supplier product revision conflict.");
    }
    private async Task<(long External, long ProductRevision)> ReadAsync(AuthenticatedAdmin actor, Guid product, Guid source, long revision, CancellationToken ct)
    {
        var row = await new SupplierInspectionStore(sessions).ReadAsync(actor, product, source, revision, ct);
        if (row is null || !long.TryParse(row.External, NumberStyles.None, CultureInfo.InvariantCulture, out long external) || external <= 0 || row.External != external.ToString(CultureInfo.InvariantCulture)) throw new InvalidOperationException("Supplier mapping is unavailable.");
        return (external, row.ProductRevision);
    }
    public void Dispose() => http.Dispose();
    private sealed class Authorization(SupplierCredentialSnapshot credentials, string account, HttpMessageHandler transport) : DelegatingHandler(transport)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method != HttpMethod.Get || request.Content is not null || request.RequestUri is not { Scheme: "https", Host: "akunding.shop", Port: 443 } uri || uri.UserInfo.Length != 0 || !Allowed(uri))
                throw new UnauthorizedAccessException("Supplier transport is unavailable.");
            credentials.UseCredential("akunding", account, key =>
            {
                if (key.Length is < 1 or > 256) throw new InvalidOperationException("Supplier credential is unavailable.");
                foreach (byte value in key) if (!char.IsAsciiLetterOrDigit((char)value) && value != (byte)'_' && value != (byte)'-') throw new InvalidOperationException("Supplier credential is unavailable.");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Encoding.ASCII.GetString(key)); return true;
            });
            try { return await base.SendAsync(request, ct); }
            finally { request.Headers.Authorization = null; }
        }
        private static bool Allowed(Uri uri)
        {
            if (uri.AbsolutePath == "/api/v1/products") return uri.Query == "?include_out_of_stock=true";
            const string prefix = "/api/v1/products/";
            return uri.Query.Length == 0 && uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)
                && long.TryParse(uri.AbsolutePath.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out long id) && id > 0
                && uri.AbsolutePath == prefix + id.ToString(CultureInfo.InvariantCulture);
        }
    }
}
