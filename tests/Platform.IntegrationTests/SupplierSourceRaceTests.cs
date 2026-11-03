using Platform.Core;
using Platform.Persistence;
using Platform.Security;
using Platform.Telegram;
using Platform.Testing;

namespace Platform.IntegrationTests;

[Collection("PostgreSQL races")]
public sealed class SupplierSourceRaceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Lazy<string> Password = new(() => AdminAuth.CreatePasswordVerifier(new string('r', 20)));
    [Theory]
    [InlineData("wallet")]
    [InlineData("free")]
    [InlineData("crypto")]
    [InlineData("bank")]
    public async Task EveryAcceptanceRouteSerializesSelectionAndKeepsOneImmutableBindingAcrossHundredReplays(string method)
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        Guid original = await SupplierFixture.ConfigureAsync(f, Ct), next = Guid.NewGuid();
        var actor = await f.AdminAsync(Ct);
        var selector = await OtherAdminAsync(f);
        var store = new SupplierSourceSelectionStore(f.Sessions);
        var payment = new PaymentSettlementFixture(f);
        await store.CreateAsync(actor, f.Product, next, "synthetic", "Public next mapping", "Public next variant", Guid.NewGuid(), Ct);
        await store.SetEnabledAsync(actor, f.Product, next, true, 1, Guid.NewGuid(), payment.Catalog, Ct);
        Guid order, paymentId = Guid.Empty;
        string? origin = null;
        decimal due = 0;
        long revision = 0;
        if (method == "crypto")
        {
            await payment.EnableAsync(Ct);
            var invoice = await payment.OrderAsync(ct: Ct);
            order = invoice.Order; paymentId = invoice.Payment; origin = invoice.Origin; due = invoice.Due;
            await payment.CaptureAsync(paymentId, origin, due, ct: Ct);
        }
        else if (method == "bank")
        {
            await payment.EnableBankAsync(Ct);
            (paymentId, order) = await payment.BankOrderAsync(Ct);
            await payment.Manual.SubmitAsync(f.Context(0), CheckoutFixture.Actor(0), paymentId, "protected:synthetic/source-race", Ct);
            await using var read = await f.Sessions.BeginAsync(Ct);
            revision = await read.QuerySingleAsync(new DbStatement("SELECT revision FROM shop.orders WHERE id=$1", DbValue.Uuid(order)), r => r.GetInt64(0), Ct);
        }
        else
        {
            order = Guid.CreateVersion7();
            if (method == "free") await SupplierRecoveryTests.Coupon(f, 100);
        }
        var service = new WalletCheckoutService(f.Sessions, f.Store, payment.Catalog);
        var request = new CheckoutRequest(order, f.Product, 1);
        Guid decision = Guid.NewGuid();
        async Task AcceptAsync()
        {
            if (method == "crypto") await payment.Store.SettleAsync(f.Context(0), paymentId, PaymentSettlementFixture.Lookup, Ct);
            else if (method == "bank") await payment.Manual.DecideAsync(f.Context(0), actor, paymentId, true, revision, decision, "Public source race decision", Ct);
            else await service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), request, (_, _, _) => Task.FromResult(ChannelMembership.Member), Ct);
        }
        Task<Exception?> accepted = Record.ExceptionAsync(AcceptAsync).AsTask();
        await Task.WhenAll(accepted, store.SelectAsync(selector, f.Product, next, 1, 2, Guid.NewGuid(), Ct));
        Exception? failure = await accepted;
        if (failure is not null)
        {
            Assert.True(method is "wallet" or "free" && failure is PricingRejectedException or UnauthorizedAccessException);
            Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders", Ct));
            await AcceptAsync();
        }
        Guid bound;
        await using (var read = await f.Sessions.BeginAsync(Ct)) bound = await read.QuerySingleAsync(new DbStatement("SELECT product_supplier_source_id FROM shop.supplier_requests WHERE order_id=$1", DbValue.Uuid(order)), r => r.GetGuid(0), Ct);
        Assert.Contains(bound, new[] { original, next });
        for (int i = 0; i < 100; i++) await AcceptAsync();
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.supplier_requests WHERE product_supplier_source_id='" + bound.ToString("D") + "' AND account_ref='public account' AND cost_baseline_amount=1", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status IN ('paid','not_required')", Ct));
        Assert.Equal(method == "wallet" ? 1L : 0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit' AND signed_amount=-10", Ct));
        Assert.Equal(method == "free" ? 1L : 0L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='held' AND units_covered=1", Ct));
        Assert.Equal(method is "crypto" or "bank" ? 1L : 0L, await f.CountAsync("SELECT count(*) FROM shop.payments WHERE status='settled'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments", Ct));
    }
    [Fact]
    public async Task DistinctAdministratorsCannotBothSelectAtOneProductRevision()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        Guid original = await SupplierFixture.ConfigureAsync(f, Ct), next = await SupplierSourceSelectionQualification.AddAsync(f, Ct);
        var a = await f.AdminAsync(Ct);
        var b = await OtherAdminAsync(f);
        var store = new SupplierSourceSelectionStore(f.Sessions);
        var errors = await Task.WhenAll(new[] { (Actor: a, Source: original), (Actor: b, Source: next) }.Select(async item => await Record.ExceptionAsync(() => store.SelectAsync(item.Actor, f.Product, item.Source, 1, 1, Guid.NewGuid(), Ct))));
        Assert.Single(errors, error => error is null);
        Assert.Single(errors, error => error is InvalidOperationException);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.audit_events WHERE action='select_source'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='evaluate_restock'", Ct));
    }
    private static async Task<AuthenticatedAdmin> OtherAdminAsync(CheckoutFixture f)
    {
        await using (var seed = await f.Sessions.BeginAsync(Ct))
        {
            await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.admins(id,login,authentication_mode,password_verifier) VALUES (gen_random_uuid(),'public-source-selector','password',$1)", DbValue.Text(Password.Value)), Ct);
            await seed.CommitAsync(Ct);
        }
        await using var login = await f.Sessions.BeginAsync(Ct);
        using var ticket = await AdminSessionStore.LoginAsync(login, "public-source-selector", new string('r', 20).AsMemory(), TimeSpan.FromHours(1), Ct);
        await login.CommitAsync(Ct);
        await using var resolve = await f.Sessions.BeginAsync(Ct);
        return await AdminSessionStore.AuthenticateAsync(resolve, ticket.Token, Ct);
    }
}
