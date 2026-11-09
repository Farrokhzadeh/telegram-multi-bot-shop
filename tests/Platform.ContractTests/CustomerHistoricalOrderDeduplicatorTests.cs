using Platform.Security;
using Platform.Telegram;

namespace Platform.ContractTests;

public sealed class CustomerHistoricalOrderDeduplicatorTests
{
    private static CustomerHistoricalOrder Order(int number, string? title = null) =>
        new("ORD-" + number.ToString("D12"), new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(number),
            2, 600000m, "IRR", title, null, true);

    [Fact]
    public void OverlappingTypedHistoryIsCountedOnceAndRemainsPaginated()
    {
        var archives = Enumerable.Range(0, 55).Select(i => Order(i)).ToArray();
        var imported = archives.Take(10).Select(i => i with { SavedTitle = "Verified original title" }).ToArray();
        var first = CustomerHistoricalOrderDeduplicator.Page(archives, imported, 0);
        var second = CustomerHistoricalOrderDeduplicator.Page(archives, imported, 20);
        var third = CustomerHistoricalOrderDeduplicator.Page(archives, imported, 40);
        var all = first.Orders.Concat(second.Orders).Concat(third.Orders).ToArray();
        Assert.Equal(55, all.Length);
        Assert.Equal(55, all.Select(x => x.OriginalReference).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(20, first.NextOffset);
        Assert.Equal(40, second.NextOffset);
        Assert.Null(third.NextOffset);
        Assert.Equal("Verified original title", Assert.Single(all.Where(x => x.OriginalReference == archives[0].OriginalReference)).SavedTitle);
        Assert.All(all, x => Assert.Equal("unknown", x.PaymentStatus));
    }

    [Fact]
    public void ForeignOrConflictingAliasNeverSilentlyReplacesOriginal()
    {
        var archived = new[] { Order(1) };
        Assert.Throws<UnauthorizedAccessException>(() => CustomerHistoricalOrderDeduplicator.Page([Order(1), Order(1)], [], 0));
        Assert.Throws<UnauthorizedAccessException>(() => CustomerHistoricalOrderDeduplicator.Page(archived, [Order(1) with { OriginalTotal = 55 }], 0));
        Assert.Throws<UnauthorizedAccessException>(() => CustomerHistoricalOrderDeduplicator.Page(archived, [Order(1) with { Quantity = 1 }], 0));
        Assert.Throws<UnauthorizedAccessException>(() => CustomerHistoricalOrderDeduplicator.Page(archived, [Order(1) with { OriginalCurrency = "USD" }], 0));
        Assert.Throws<UnauthorizedAccessException>(() => CustomerHistoricalOrderDeduplicator.Page(archived, [Order(1) with { CreatedAt = DateTime.UtcNow }], 0));
        Assert.Throws<UnauthorizedAccessException>(() => CustomerHistoricalOrderDeduplicator.Page(archived, [Order(1) with { OriginalReference = "ORD-1\nInjected" }], 0));
        Assert.Throws<UnauthorizedAccessException>(() => CustomerHistoricalOrderDeduplicator.Page([Order(1, "Original")], [Order(1, "Changed")], 0));
    }

    [Fact]
    public void ImportedHistoricalOnlyRecordAppearsOnceWithoutErasingOriginals()
    {
        var result = CustomerHistoricalOrderDeduplicator.Page([Order(2)], [Order(1)], 0);
        Assert.Equal(2, result.Orders.Count);
        Assert.Null(result.NextOffset);
        Assert.Equal("ORD-000000000002", result.Orders[0].OriginalReference);
    }
}
