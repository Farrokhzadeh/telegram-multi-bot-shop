using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api;
using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class PaymentReadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task PaymentStatusHttpRequiresCurrentIndependentAdminAndRedactsEvidenceAndCredentials()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, ct: Ct);
        var fixture = new PaymentSettlementFixture(f);
        await fixture.EnableBankAsync(Ct);
        var order = await fixture.BankOrderAsync(Ct);
        await fixture.Manual.SubmitAsync(f.Context(0), CheckoutFixture.Actor(0), order.Payment, "protected:synthetic/private-receipt", Ct);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.UseRouting();
        app.UseMiddleware<AdminGuardMiddleware>((Func<AdminToken, CancellationToken, Task<AuthenticatedAdmin?>>)(async (token, ct) =>
        { await using var session = await f.Sessions.BeginAsync(ct); return await AdminSessionStore.AuthenticateAsync(session, token, ct); }));
        app.MapPaymentStatus(f.Sessions);
        app.MapManualDepositStatus(f.Sessions);
        await app.StartAsync(Ct);
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        string route = $"/v1/admin/manual-deposits/{order.Payment:D}?brandId={f.Brands[0]:D}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(route, Ct)).StatusCode);
        using var issued = await Login(f);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued.Token.ExportForTransport());
        string response = await client.GetStringAsync(route, Ct);
        Assert.Contains("observed", response, StringComparison.Ordinal);
        foreach (string secret in new[] { "1111222233334444", "holder", "protected:", "ciphertext", "ipn", "private-receipt", "provider_payment_ref" }) Assert.DoesNotContain(secret, response, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(route.Replace(f.Brands[0].ToString("D"), f.Brands[1].ToString("D"), StringComparison.Ordinal), Ct)).StatusCode);
        await using (var revoke = await f.Sessions.BeginAsync(Ct))
        { await revoke.ExecuteAsync(new DbStatement("UPDATE shop.admin_sessions SET revoked_at=clock_timestamp() WHERE id=$1", DbValue.Uuid(issued.Id)), Ct); await revoke.CommitAsync(Ct); }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(route, Ct)).StatusCode);
        await app.StopAsync(Ct);
    }

    [Fact]
    public async Task CustomerPaymentReadUsesOriginalMembershipAndReportsInternalUsdCreditOnly()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, ct: Ct);
        var fixture = new PaymentSettlementFixture(f);
        await fixture.EnableAsync(Ct);
        var old = await fixture.OrderAsync(ct: Ct);
        await fixture.OrderAsync(ct: Ct);
        await fixture.CaptureAsync(old.Payment, old.Origin, old.Due, ct: Ct);
        await fixture.Store.SettleAsync(f.Context(0), old.Payment, PaymentSettlementFixture.Lookup, Ct);
        await using var read = await f.Sessions.BeginAsync(Ct);
        var view = await PaymentReadStore.ForCustomerAsync(read, f.Context(0), CheckoutFixture.Actor(0), old.Payment, Ct);
        Assert.NotNull(view);
        Assert.Equal("10", view.WalletCreditUsd);
        Assert.Equal("late_crypto_credit", view.WalletMovement);
        Assert.Equal("cancelled", view.OrderPaymentState);
        Assert.Null(await PaymentReadStore.ForCustomerAsync(read, f.Context(1), CheckoutFixture.Actor(1), old.Payment, Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments", Ct));
    }
    private static async Task<IssuedAdminSession> Login(CheckoutFixture f)
    {
        await using var session = await f.Sessions.BeginAsync(Ct);
        var issued = await AdminSessionStore.LoginAsync(session, "public-checkout-admin", new string('s', 20).AsMemory(), TimeSpan.FromHours(1), Ct);
        await session.CommitAsync(Ct);
        return issued;
    }
}
