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
    public AkundingSourceInspection(DbSessionFactory sessions, SecretSnapshot commerce, SupplierCredentialSnapshot credentials, string account, HttpMessageHandler? transport = null)
    {
        if (credentials.DeploymentId != commerce.DeploymentId || credentials.Service != commerce.Service) throw new UnauthorizedAccessException("Supplier credential scope is unavailable.");
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
    private async Task<(long External, long ProductRevision)> ReadAsync(AuthenticatedAdmin actor, Guid product, Guid source, long revision, CancellationToken ct)
    {
        await using var session = await sessions.BeginAsync(ct);
        await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
        var rows = await session.QueryManyAsync(new DbStatement("""
            SELECT s.external_product_id,p.revision FROM shop.product_supplier_sources s JOIN shop.products p ON p.id=s.product_id
            WHERE s.id=$1 AND s.product_id=$2 AND s.revision=$3 AND s.provider_code='akunding'
                AND s.external_variant_id IS NULL AND p.fulfillment_kind='supplier'
            """, DbValue.Uuid(source), DbValue.Uuid(product), DbValue.Bigint(revision)), r => (External: r.GetString(0), ProductRevision: r.GetInt64(1)), 1, ct);
        if (rows.Count != 1 || !long.TryParse(rows[0].External, NumberStyles.None, CultureInfo.InvariantCulture, out long external) || external <= 0 || rows[0].External != external.ToString(CultureInfo.InvariantCulture))
            throw new InvalidOperationException("Supplier mapping is unavailable.");
        return (external, rows[0].ProductRevision);
    }
    public void Dispose() => http.Dispose();
    private sealed class Authorization(SupplierCredentialSnapshot credentials, string account, HttpMessageHandler transport) : DelegatingHandler(transport)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method != HttpMethod.Get || request.Content is not null || request.RequestUri is not { Scheme: "https", Host: "akunding.shop", Port: 443 } uri || !uri.AbsolutePath.StartsWith("/api/v1/products/", StringComparison.Ordinal) || uri.Query.Length != 0 || uri.UserInfo.Length != 0)
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
    }
}
