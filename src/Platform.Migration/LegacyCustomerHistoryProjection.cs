using System.Globalization;
using Platform.Security;

namespace Platform.Migration;

public sealed class LegacyCustomerHistoryProjection(SecretSnapshot secrets)
{
    public IReadOnlyDictionary<Guid, byte[]> EncodeAll(BrandContext context, VerifiedImportManifest manifest, IReadOnlyList<ArchivedLegacyRecord> source)
    {
        if (context.BrandId != manifest.TargetBrand || source is null) throw new MigrationRejectedException();
        var owners = manifest.Proofs.Where(p => p.Projection == LegacyProjection.User).ToDictionary(p => p.LegacyId, p => p.Target, StringComparer.Ordinal);
        var expected = manifest.Proofs.Where(p => p.Projection == LegacyProjection.OrderRecord).ToArray();
        var orders = source.Where(row => row.Source.Projection == LegacyProjection.OrderRecord).ToArray();
        if (orders.Length != expected.Length || orders.Select(o => o.Source.Id).Distinct(StringComparer.Ordinal).Count() != orders.Length || owners.Count == 0 || owners.Values.Distinct().Count() != owners.Count) throw new MigrationRejectedException();
        var grouped = owners.Values.ToDictionary(id => id, _ => new List<CustomerHistoricalOrder>());
        var reader = new LegacyReadOnlyOrderHistory(secrets);
        foreach (var row in orders)
        {
            try
            {
                using var original = row.Source.Open();
                var id = original.RootElement.GetProperty("user_id").GetInt64().ToString(CultureInfo.InvariantCulture);
                if (!owners.TryGetValue(id, out var member)) throw new MigrationRejectedException();
                var view = reader.ReadCustomer(context, manifest, member, row, row.Evidence);
                grouped[member].Add(new(view.OriginalReference, view.CreatedAt, view.Quantity, view.OriginalTotal, view.OriginalCurrency, view.SavedTitle, view.SavedPaymentMethod, view.IncompleteTerms));
            }
            catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException or FormatException)
            { throw new MigrationRejectedException(); }
        }
        return grouped.ToDictionary(pair => pair.Key, pair => CustomerHistoryContent.Encode(pair.Key, pair.Value));
    }
}
