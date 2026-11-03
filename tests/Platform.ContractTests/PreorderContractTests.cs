using Platform.Core;
using Platform.Persistence;

namespace Platform.ContractTests;

public sealed class PreorderContractTests
{
    [Fact]
    public void AdminCatalogAndAcceptedQuoteExposeNoFulfillmentDateOrDuration()
    {
        Type[] dates = [typeof(DateTime), typeof(DateTime?), typeof(TimeSpan), typeof(TimeSpan?)];
        var properties = typeof(MasterProductEdit).GetProperties().Concat(typeof(AdminProduct).GetProperties()).Concat(typeof(CatalogQuery).GetProperties()).Concat(typeof(CatalogItem).GetProperties()).Concat(typeof(QuoteTerms).GetProperties());
        Assert.DoesNotContain(properties, p => dates.Contains(p.PropertyType));
        Assert.Equal(new[] { nameof(JobKind.ExpireOrderPayment) }, Enum.GetNames<JobKind>().Where(n => n.Contains("Expir", StringComparison.OrdinalIgnoreCase)));
    }
}
