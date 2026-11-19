using Platform.Persistence;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class CustomerScreensTests
{
    [Fact]
    public async Task ProfileLanguageAndCommerceHistoryUseTheVerifiedMembership()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var setup = await CheckoutFixture.CreateAsync(users: 4, stock: 2, ct: ct);
        var screens = new CustomerScreens(setup.Sessions);
        var brand = setup.Context(0);
        long owner = CheckoutFixture.Actor(0);
        long otherMember = CheckoutFixture.Actor(3);

        Assert.Equal(100m, (await screens.HomeAsync(brand, owner, ct)).Balance);
        await screens.SetLanguageAsync(brand, owner, "fa", ct);
        Assert.Equal("fa", (await screens.HomeAsync(brand, owner, ct)).Language);
        Assert.Equal("en", (await screens.HomeAsync(brand, otherMember, ct)).Language);

        var order = await setup.Store.PurchaseLocalAsync(brand, owner, setup.Request(), ct);
        Assert.Equal(CheckoutOutcome.Purchased, order.Outcome);
        var summary = await screens.HomeAsync(brand, owner, ct);
        Assert.Equal(1L, summary.Orders);
        Assert.Equal(90m, summary.Balance);
        var list = await screens.ListAsync(brand, owner, CustomerScreenList.Orders, "all", 0, ct);
        Assert.Equal(order.OrderId!.Value, Assert.Single(list.Entries).Id);
        var detail = await screens.DetailAsync(brand, owner, CustomerScreenList.Orders, order.OrderId!.Value, ct);
        Assert.Equal(10m, detail!.Amount);
        Assert.Null(await screens.DetailAsync(brand, otherMember, CustomerScreenList.Orders, order.OrderId.Value, ct));
        Assert.Null(await screens.DetailAsync(setup.Context(1), owner, CustomerScreenList.Orders, order.OrderId.Value, ct));
        var transactions = await screens.ListAsync(brand, owner, CustomerScreenList.Transactions, "all", 0, ct);
        Assert.Contains(transactions.Entries, t => t.Title == "purchase_debit" && t.Amount == -10m);
        Assert.Empty((await screens.ListAsync(brand, owner, CustomerScreenList.Deposits, "all", 0, ct)).Entries);
        Assert.Empty((await screens.ListAsync(brand, owner, CustomerScreenList.Discounts, "all", 0, ct)).Entries);
    }
}
