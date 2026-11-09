using Platform.Security;
using Platform.Telegram;

namespace Platform.ContractTests;

public sealed class CustomerHistoryMessageTests
{
    private static CustomerHistoricalOrder Order(int id, string currency = "IRR", string? title = null) =>
        new("ORD-" + id.ToString("D12"), new DateTime(2024, 2, 1, 12, 30, 0, DateTimeKind.Utc), 2, currency == "IRR" ? 600000 : 12.25m, currency, title, null, true);

    [Fact]
    public void ReadOnlyHistoryMessagesPreserveOriginalCurrencyAndExplicitlyUnknownStatuses()
    {
        var page = new CustomerHistoricalPage([Order(1), Order(2, "USD", "Saved original title")], null);
        var result = new UpdateRouteResult(UpdateRouteCode.History, 0, History: page);
        var message = Assert.Single(CustomerHistoryMessageRenderer.Render(result));
        Assert.Null(message.ParseMode);
        Assert.False(message.HasActionButtons);
        Assert.Contains("600000 IRR", message.Text, StringComparison.Ordinal);
        Assert.Contains("12.25 USD", message.Text, StringComparison.Ordinal);
        Assert.Contains("Product: Unknown", message.Text, StringComparison.Ordinal);
        Assert.Contains("Product: Saved original title", message.Text, StringComparison.Ordinal);
        Assert.Contains("Payment status: Unknown", message.Text, StringComparison.Ordinal);
        Assert.Contains("Delivery status: Unknown", message.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Approved", message.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Refund", message.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Deliver now", message.Text, StringComparison.Ordinal);
        Assert.Equal("CustomerHistoryMessage [redacted]", message.ToString());
    }

    [Fact]
    public void TwentyLargeRecordsSplitWithoutOverflowOrDroppingAnyOriginalReference()
    {
        var page = new CustomerHistoricalPage(Enumerable.Range(1, 20).Select(i => Order(i, title: new string('X', 160))).ToArray(), 20);
        var parts = CustomerHistoryMessageRenderer.Render(page);
        Assert.True(parts.Count > 1);
        Assert.All(parts, p => { Assert.InRange(p.Text.Length, 1, 3500); Assert.Null(p.ParseMode); Assert.False(p.HasActionButtons); });
        var text = string.Concat(parts.Select(p => p.Text));
        foreach (int i in Enumerable.Range(1, 20)) Assert.Equal(1, text.Split("Order: ORD-" + i.ToString("D12"), StringSplitOptions.None).Length - 1);
        Assert.Equal(1, text.Split("Next page: /orders 20", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void MalformedPagesAndUntrustedOriginalsRejectWithoutRendering()
    {
        Assert.Throws<ArgumentException>(() => CustomerHistoryMessageRenderer.Render(new UpdateRouteResult(UpdateRouteCode.Order, 0)));
        Assert.Throws<ArgumentException>(() => CustomerHistoryMessageRenderer.Render(new CustomerHistoricalPage([], 21)));
        Assert.Throws<ArgumentException>(() => CustomerHistoryMessageRenderer.Render(new CustomerHistoricalPage(Enumerable.Repeat(Order(1), 21).ToArray(), null)));
        Assert.Throws<ArgumentException>(() => CustomerHistoryMessageRenderer.Render(new CustomerHistoricalPage([Order(1) with { OriginalReference = "ORD-1\nPay" }], null)));
        Assert.Throws<ArgumentException>(() => CustomerHistoryMessageRenderer.Render(new CustomerHistoricalPage([Order(1) with { SavedTitle = "Unsafe\nStatus" }], null)));
        Assert.Throws<ArgumentException>(() => CustomerHistoryMessageRenderer.Render(new CustomerHistoricalPage([Order(1) with { OriginalCurrency = "TOMAN" }], null)));
    }

    [Fact]
    public void EmptyPageShowsNoOrdersAndNeverCreatesActions()
    {
        var response = Assert.Single(CustomerHistoryMessageRenderer.Render(new CustomerHistoricalPage([], null)));
        Assert.Contains("No historical orders found.", response.Text, StringComparison.Ordinal);
        Assert.Null(response.ParseMode);
        Assert.False(response.HasActionButtons);
    }
}
