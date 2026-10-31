using Platform.Testing;

namespace Platform.ContractTests;

public sealed class TransportUncertaintyTests
{
    [Fact]
    public async Task EditRecoveryUsesOneVisibleOrderMessageAfterLostReply()
    {
        using var transport = new FakeTelegramTransport();
        using var client = new HttpClient(transport) { BaseAddress = new Uri("https://synthetic.invalid/") };
        using var anchor = await client.PostAsync("sendMessage", null, TestContext.Current.CancellationToken);
        transport.LoseNextReply = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => client.PostAsync("editMessageText", null, TestContext.Current.CancellationToken));
        using var retry = await client.PostAsync("editMessageText", null, TestContext.Current.CancellationToken);
        Assert.True(retry.IsSuccessStatusCode);
        Assert.Equal(1, transport.VisibleMessages);
        Assert.Equal(2, transport.Edits);
    }

    [Fact]
    public async Task LostSendReplyDoesNotProveTheMessageWasAbsent()
    {
        using var transport = new FakeTelegramTransport { LoseNextReply = true };
        using var client = new HttpClient(transport) { BaseAddress = new Uri("https://synthetic.invalid/") };
        await Assert.ThrowsAsync<HttpRequestException>(() => client.PostAsync("sendMessage", null, TestContext.Current.CancellationToken));
        Assert.Equal(1, transport.VisibleMessages);
    }

    [Fact]
    public async Task SupplierLookupReconcilesTheOriginalUnknownPurchase()
    {
        using var transport = new FakeSupplierTransport { LoseNextPurchaseReply = true };
        using var client = new HttpClient(transport) { BaseAddress = new Uri("https://synthetic.invalid/") };
        await Assert.ThrowsAsync<HttpRequestException>(() => client.PostAsync("purchase", null, TestContext.Current.CancellationToken));
        using var lookup = await client.GetAsync("purchase/original", TestContext.Current.CancellationToken);
        Assert.Equal("accepted-original", await lookup.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, transport.Purchases);
    }
}
