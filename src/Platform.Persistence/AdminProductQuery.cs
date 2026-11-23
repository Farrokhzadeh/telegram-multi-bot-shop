namespace Platform.Persistence;

public sealed partial class DbSession
{
    internal Task<List<AdminProduct>> ListAdminProductsAsync(Guid? after, int count, CancellationToken ct) =>
        RunEfStepAsync(false, (data, token) => CatalogAdministration.ProductsAsync(data, after, count, token), ct);
}
