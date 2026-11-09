using System.Net;
using System.Security.Cryptography;
using System.Text;
using Platform.Persistence;
using Platform.Security;
using Platform.Telegram;
using Platform.Testing;

namespace Platform.IntegrationTests;

[Collection("PostgreSQL races")]
public sealed class CustomerHistoryPrivateRoutingTests
{
    private sealed class HistoryReply(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent("""{"ok":true,"result":{"message_id":77,"chat":{"id":7001,"type":"private"}}}""")
            });
        }
    }
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static VerifiedTelegramUpdate Inbound(BrandContext brand, long actor, string command)
    {
        byte[] secret = SHA256.HashData("public synthetic private historical ingress"u8);
        using var verifier = new TelegramWebhookVerifier(brand, secret);
        string header = Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string body = "{\"update_id\":34,\"message\":{\"message_id\":77,\"from\":{\"id\":" +
            actor + ",\"is_bot\":false},\"chat\":{\"id\":" + actor + ",\"type\":\"private\"},\"text\":\"" + command + "\"}}";
        return verifier.Verify([header], Encoding.UTF8.GetBytes(body));
    }
    private static CustomerHistoricalOrder[] Orders(int member, int count) =>
        Enumerable.Range(0, count).Select(i => new CustomerHistoricalOrder("SOURCE-" + member + "-" + i.ToString("D4"),
            new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i), 1, 1000 + member * 10 + i,
            member % 2 == 0 ? "IRR" : "USD", null, null, true)).ToArray();

    [Fact]
    public async Task AuthenticatedReadOnlyHistoryCannotCrossMemberOrBrandAndPreservesPagination()
    {
        var ct = Ct;
        await using var f = await CheckoutFixture.CreateAsync(users: 6, balance: 0, stock: 0, ct: ct);
        string directory = Path.Combine(Path.GetTempPath(), "teleg-customer-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var archive = new CustomerHistoryArchive(f.Secrets);
            var store = new CustomerHistoryFiles(directory, f.Secrets);
            int[] counts = [25, 3, 0, 2, 0, 1];
            for (int i = 0; i < counts.Length; i++)
                await store.PublishAsync(f.Context(i), f.Users[i], archive.Seal(f.Context(i), f.Users[i], Orders(i, counts[i])), ct);
            var router = new CustomerHistoryCommandRouter(f.Sessions, archive, store);
            var first = await router.ReadAsync(Inbound(f.Context(0), CheckoutFixture.Actor(0), "/orders"), ct);
            var second = await router.ReadAsync(Inbound(f.Context(0), CheckoutFixture.Actor(0), "/orders 20"), ct);
            Assert.Equal(20, first.Orders.Count);
            Assert.Equal(20, first.NextOffset);
            Assert.Equal(5, second.Orders.Count);
            Assert.Null(second.NextOffset);
            Assert.Equal(25, first.Orders.Concat(second.Orders).Select(o => o.OriginalReference).Distinct(StringComparer.Ordinal).Count());
            Assert.All(first.Orders.Concat(second.Orders), o =>
            {
                Assert.StartsWith("SOURCE-0-", o.OriginalReference, StringComparison.Ordinal);
                Assert.Equal("IRR", o.OriginalCurrency);
                Assert.Equal("unknown", o.PaymentStatus);
                Assert.Equal("unknown", o.DeliveryStatus);
                Assert.True(o.ReadOnly);
            });
            var response = new HistoryReply();
            using (var http = new HttpClient(response))
            {
                var delivery = new CustomerHistoryDeliveryPipeline(router, new CustomerHistoryTelegramSender(http));
                string token = f.Context(0).BotId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + new string('x', 40);
                var receipt = await delivery.SendAsync(token, Inbound(f.Context(0), CheckoutFixture.Actor(0), "/orders"), ct);
                Assert.True(receipt.Completed);
                Assert.True(receipt.ConfirmedMessages > 0);
                Assert.Equal(receipt.ConfirmedMessages, response.Calls);
                int before = response.Calls;
                string foreign = f.Context(1).BotId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + new string('x', 40);
                await Assert.ThrowsAsync<TelegramIngressRejectedException>(() => delivery.SendAsync(foreign, Inbound(f.Context(0), CheckoutFixture.Actor(0), "/orders"), ct));
                Assert.Equal(before, response.Calls);
            }
            var uncertain = new HistoryReply(HttpStatusCode.ServiceUnavailable);
            using (var http = new HttpClient(uncertain))
            {
                var delivery = new CustomerHistoryDeliveryPipeline(router, new CustomerHistoryTelegramSender(http));
                string token = f.Context(0).BotId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + new string('x', 40);
                var receipt = await delivery.SendAsync(token, Inbound(f.Context(0), CheckoutFixture.Actor(0), "/orders"), ct);
                Assert.False(receipt.Completed);
                Assert.Equal(CustomerHistorySendState.Unknown, receipt.FinalState);
                Assert.Equal(0, receipt.ConfirmedMessages);
                Assert.Equal(1, uncertain.Calls);
            }
            var sameActorOtherBrand = await router.ReadAsync(Inbound(f.Context(1), CheckoutFixture.Actor(1), "/orders"), ct);
            Assert.Equal(3, sameActorOtherBrand.Orders.Count);
            Assert.All(sameActorOtherBrand.Orders, o => Assert.StartsWith("SOURCE-1-", o.OriginalReference, StringComparison.Ordinal));
            var noOrders = await router.ReadAsync(Inbound(f.Context(2), CheckoutFixture.Actor(2), "/orders"), ct);
            Assert.Empty(noOrders.Orders);
            var anotherMemberSameBrand = await router.ReadAsync(Inbound(f.Context(3), CheckoutFixture.Actor(3), "/orders"), ct);
            Assert.Equal(2, anotherMemberSameBrand.Orders.Count);
            Assert.All(anotherMemberSameBrand.Orders, o => Assert.StartsWith("SOURCE-3-", o.OriginalReference, StringComparison.Ordinal));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => router.ReadAsync(Inbound(f.Context(0), 7004, "/orders"), ct));
            var cipher = await store.ReadAsync(f.Context(0), f.Users[0], ct);
            Assert.Throws<UnauthorizedAccessException>(() => archive.Open(f.Context(1), f.Users[1], cipher.Span));
            await using (var session = await f.Sessions.BeginAsync(ct))
            {
                await session.ExecuteAsync(new DbStatement("UPDATE shop.users SET blocked_at=clock_timestamp() WHERE id=$1", DbValue.Uuid(f.Users[0])), ct);
                await session.CommitAsync(ct);
            }
            await Assert.ThrowsAsync<TelegramIngressRejectedException>(() => router.ReadAsync(Inbound(f.Context(0), CheckoutFixture.Actor(0), "/orders"), ct));
            await Assert.ThrowsAsync<TelegramIngressRejectedException>(() => router.ReadAsync(Inbound(f.Context(1), CheckoutFixture.Actor(1), "/orders 21"), ct));
            await using (var session = await f.Sessions.BeginAsync(ct))
            {
                await session.ExecuteAsync(new DbStatement("UPDATE shop.users SET banned_at=clock_timestamp() WHERE id=$1", DbValue.Uuid(f.Users[3])), ct);
                await session.CommitAsync(ct);
            }
            await Assert.ThrowsAsync<TelegramIngressRejectedException>(() => router.ReadAsync(Inbound(f.Context(3), CheckoutFixture.Actor(3), "/orders"), ct));
            await using (var older = await f.Sessions.BeginReadOnlyAsync(ct))
            {
                var before = await BrandUserStore.RequireAsync(older, f.Context(5), CheckoutFixture.Actor(5), f.Users[5], ct);
                Assert.False(before.Banned);
                await using (var session = await f.Sessions.BeginAsync(ct))
                {
                    await session.ExecuteAsync(new DbStatement("UPDATE shop.users SET banned_at=clock_timestamp() WHERE id=$1", DbValue.Uuid(f.Users[5])), ct);
                    await session.CommitAsync(ct);
                }
                var stale = await BrandUserStore.RequireAsync(older, f.Context(5), CheckoutFixture.Actor(5), f.Users[5], ct);
                Assert.False(stale.Banned);
                await using var fresh = await f.Sessions.BeginReadOnlyAsync(ct);
                var changed = await BrandUserStore.RequireAsync(fresh, f.Context(5), CheckoutFixture.Actor(5), f.Users[5], ct);
                Assert.True(changed.Banned);
            }
            await Assert.ThrowsAsync<TelegramIngressRejectedException>(() => router.ReadAsync(Inbound(f.Context(5), CheckoutFixture.Actor(5), "/orders"), ct));
        }
        finally { Directory.Delete(directory, true); }
    }
}
