using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api;
using Platform.Core;
using Platform.Persistence;
using Platform.Security;
using Platform.Telegram;
using Platform.Testing;

namespace Platform.ContractTests;

public sealed class AdminApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task UnrestrictedOwnerEditsPreserveActualHistoryAndAuditOldNewIncludingPaidEntitlements()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 4, ct: Ct);
        var actor = await f.AdminAsync(Ct);
        var pricing = new PricingStore(f.Sessions);
        var service = new WalletCheckoutService(f.Sessions, f.Store, new CatalogStore(f.Sessions, [], (_, _) => throw new InvalidOperationException()));
        Guid coupon = Guid.NewGuid();
        await using (var issue = await f.Sessions.BeginAsync(Ct))
        {
            await pricing.IssueAsync(issue, f.Context(0), actor, new(coupon, "PUBLIC-OWNER-CONTRACT", CouponKind.Promotion, 100, f.Product, f.Users[0], MaximumUses: 4), Guid.NewGuid(), Ct);
            await issue.CommitAsync(Ct);
        }
        var first = await service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), new(Guid.CreateVersion7(), f.Product, 1), Member, Ct);
        JobLease lease;
        await using (var claim = await f.Sessions.BeginAsync(Ct))
        { lease = (await OutboxStore.ClaimAsync(claim, JobKind.PrepareManifest, Guid.NewGuid(), TimeSpan.FromSeconds(30), Ct))!; await claim.CommitAsync(Ct); }
        await new DeliveryStore(f.Sessions).PrepareAsync(f.Context(0), lease, Ct);
        await service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), new(Guid.CreateVersion7(), f.Product, 1), Member, Ct);
        var edit = new CouponOwnerEdit(10, 0, 0, 0, DateTime.UtcNow.AddDays(-2), DateTime.UtcNow.AddDays(-1), false, f.Product, f.Users[0]);
        Guid command = Guid.NewGuid();
        Assert.Equal(1, await pricing.EditAsync(f.Context(0), actor, coupon, 0, edit, AuditReason.OperatorReview, command, Ct));
        for (int i = 0; i < 25; i++) Assert.Equal(1, await pricing.EditAsync(f.Context(0), actor, coupon, 0, edit, AuditReason.OperatorReview, command, Ct));
        var conflict = await Assert.ThrowsAsync<DbOperationException>(() => pricing.EditAsync(f.Context(0), actor, coupon, 0, edit with { Percent = 20 }, AuditReason.OperatorReview, command, Ct));
        Assert.Equal(DbFailureKind.InvalidData, conflict.Failure.Kind);
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='redeemed' AND units_covered=1", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='held'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discounts WHERE redeemed_count=2 AND usage_adjustment_units=-2 AND max_total_uses=0 AND NOT active", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_revisions WHERE revision_no=0 AND percent_off=100 AND max_total_uses=4", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_revisions r JOIN shop.audit_events a ON a.id=r.audit_event_id WHERE r.revision_no=1 AND r.percent_off=10 AND r.max_total_uses=0 AND r.held_units_at_revision=0 AND a.actor_kind='admin' AND a.reason='operator_review' AND a.previous_revision=0 AND a.new_revision=1", Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => pricing.EditAsync(f.Context(0), actor, coupon, 1, edit with { MemberId = f.Users[1] }, AuditReason.OperatorReview, Guid.NewGuid(), Ct));
        await service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), new(Guid.CreateVersion7(), f.Product, 3, Kind: CheckoutKind.PrepaidUnits), Member, Ct);
        Guid prepaid;
        await using (var read = await f.Sessions.BeginAsync(Ct)) prepaid = await read.QuerySingleAsync(new DbStatement("SELECT id FROM shop.discounts WHERE kind='prepaid_entitlement'"), r => r.GetGuid(0), Ct);
        Assert.Equal(1, await pricing.EditAsync(f.Context(0), actor, prepaid, 0, edit with { Percent = 25, EffectiveUsed = 50 }, AuditReason.OperatorReview, Guid.NewGuid(), Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discounts WHERE kind='prepaid_entitlement' AND percent_off=25 AND max_total_uses=0 AND usage_adjustment_units=50 AND ends_at IS NOT NULL AND NOT active", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE order_kind='prepaid_units' AND payment_status='paid'", Ct));
    }

    [Fact]
    public async Task BrandHttpWritesRequireIndependentAdminRedactTokenAndRejectChangedReplayAndRevocation()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        await f.AdminAsync(Ct);
        var store = new BrandAdministrationStore(f.Sessions);
        int checks = 0;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.UseRouting();
        app.UseMiddleware<DiagnosticsMiddleware>((Action<string>)(_ => { }));
        app.UseMiddleware<AdminGuardMiddleware>((Func<AdminToken, CancellationToken, Task<AuthenticatedAdmin?>>)(async (token, ct) =>
        { await using var session = await f.Sessions.BeginAsync(ct); return await AdminSessionStore.AuthenticateAsync(session, token, ct); }));
        app.MapBrandAdministration(store, id => f.Contexts.SingleOrDefault(c => c.BrandId == id), (token, _) =>
        { checks++; return Task.FromResult(new VerifiedBotProfile(token.BotId, "public_test_bot")); }, (_, _, _) => Task.FromResult(true));
        await app.StartAsync(Ct);
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        string route = $"/v1/admin/brands/{f.Brands[0]:D}/bot-token", tokenValue = "1001:" + new string('s', 35);
        Guid command = Guid.NewGuid();
        string body = $"{{\"Token\":\"{tokenValue}\",\"ExpectedRevision\":0,\"CommandId\":\"{command:D}\"}}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await Put(body)).StatusCode);
        using var issued = await Login(f);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued.Token.ExportForTransport());
        using var saved = await Put(body);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        string response = await saved.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(tokenValue, response, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await Put(body)).StatusCode);
        Assert.Equal(1, checks);
        Assert.Equal(HttpStatusCode.Conflict, (await Put(body.Replace(new string('s', 35), new string('t', 35), StringComparison.Ordinal))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put("{")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(body.Replace("ExpectedRevision", "UnrecognizedRevision", StringComparison.Ordinal))).StatusCode);
        string metadata = await client.GetStringAsync($"/v1/admin/brands/{f.Brands[0]:D}", Ct);
        foreach (string privateValue in new[] { tokenValue, "ciphertext", "KeyId" }) Assert.DoesNotContain(privateValue, metadata, StringComparison.OrdinalIgnoreCase);
        await using (var revoke = await f.Sessions.BeginAsync(Ct))
        { await revoke.ExecuteAsync(new DbStatement("UPDATE shop.admin_sessions SET revoked_at=clock_timestamp()"), Ct); await revoke.CommitAsync(Ct); }
        Assert.Equal(HttpStatusCode.Unauthorized, (await Put(body)).StatusCode);
        Assert.Equal(1, checks);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.audit_events WHERE reason='credential_rotation'", Ct));
        await app.StopAsync(Ct);
        Task<HttpResponseMessage> Put(string json) => client.PutAsync(route, new StringContent(json, Encoding.UTF8, "application/json"), Ct);
    }
    private static Task<ChannelMembership> Member(long chat, long actor, CancellationToken ct) => Task.FromResult(ChannelMembership.Member);
    private static async Task<IssuedAdminSession> Login(CheckoutFixture f)
    {
        await using var session = await f.Sessions.BeginAsync(Ct);
        var issued = await AdminSessionStore.LoginAsync(session, "public-checkout-admin", new string('s', 20).AsMemory(), TimeSpan.FromHours(1), Ct);
        await session.CommitAsync(Ct);
        return issued;
    }
}
