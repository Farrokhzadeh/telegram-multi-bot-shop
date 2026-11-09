using Platform.Migration;
using Platform.Telegram;
using Platform.Testing;

namespace Platform.IntegrationTests;

[Collection("PostgreSQL races")]
public sealed class CustomerHistoricalTypedAliasTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("wallet")]
    [InlineData("free")]
    [InlineData("bank")]
    public async Task MigratedHistoricalAliasMatchesOriginalAndIsMemberAndBrandBound(string route)
    {
        await using var world = await LegacyRecordFixture.CreateAsync(route, Ct);
        var f = world.Fixture;
        var row = Assert.Single(world.Set.Orders);
        var original = new LegacyReadOnlyOrderHistory(f.Secrets).ReadCustomer(f.Context(0), world.Manifest,
            row.Member, row.Record, row.Record.Evidence);
        var archival = new Platform.Security.CustomerHistoricalOrder(original.OriginalReference, original.CreatedAt,
            original.Quantity, original.OriginalTotal, original.OriginalCurrency, original.SavedTitle,
            original.SavedPaymentMethod, original.IncompleteTerms);
        await using var session = await f.Sessions.BeginReadOnlyAsync(Ct);
        var aliases = await CustomerHistoricalTypedAliasReader.ReadAsync(session, f.Context(0), row.Member, Ct);
        var match = Assert.Single(aliases);
        Assert.Equal(archival.OriginalReference, match.OriginalReference);
        Assert.Equal(archival.OriginalCurrency, match.OriginalCurrency);
        Assert.Equal(archival.OriginalTotal, match.OriginalTotal);
        Assert.Equal(archival.CreatedAt, match.CreatedAt);
        var page = CustomerHistoricalOrderDeduplicator.Page([archival], aliases, 0, requireArchiveMatch: true);
        Assert.Single(page.Orders);
        Assert.Null(page.NextOffset);
        Assert.Equal("unknown", page.Orders[0].PaymentStatus);
        Assert.Equal("unknown", page.Orders[0].DeliveryStatus);
        Assert.Empty(await CustomerHistoricalTypedAliasReader.ReadAsync(session, f.Context(1), row.Member, Ct));
        Assert.Empty(await CustomerHistoricalTypedAliasReader.ReadAsync(session, f.Context(0), Guid.NewGuid(), Ct));
    }
}
