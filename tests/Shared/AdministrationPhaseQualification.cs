using Platform.Campaigns;
using Platform.Persistence;
using Platform.Security;

namespace Platform.Testing;

public static class AdministrationPhaseQualification
{
    public static async Task RunAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, stock: 0, ct: ct);
        var admin = await f.AdminAsync(ct);
        var catalog = new AdminCatalogStore(f.Sessions, f.Secrets);
        var orders = new OrderAdministrationStore(f.Sessions, f.Secrets);
        Guid item = Guid.CreateVersion7(), import = Guid.NewGuid();
        InventoryImportItem[] stock = [new(item, "Public synthetic phase credential")];
        await catalog.ImportAsync(admin, f.Product, stock, import, ct);
        await catalog.ImportAsync(admin, f.Product, stock, import, ct);
        var adjustment = new WalletAdjustment(f.Users[0], "12.34", 0, "Public phase qualification", Guid.NewGuid());
        await orders.AdjustAsync(admin, f.Context(0), adjustment, ct);
        await orders.AdjustAsync(admin, f.Context(0), adjustment, ct);
        if (await f.CountAsync("SELECT count(*) FROM shop.inventory_items", ct) != 1 || await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE signed_amount=12.34", ct) != 1) throw new InvalidOperationException("Administration posting replay failed.");
        var campaigns = new CampaignStore(f.Sessions);
        Guid batch = Guid.NewGuid();
        await campaigns.CreateAsync(admin, batch, [new(Guid.NewGuid(), f.Brands[0], "en", "all", null, null, "Public phase message", null)], ct);
        await campaigns.TransitionAsync(admin, batch, false, Guid.NewGuid(), "Public phase approval", ct);
        JobLease materialize;
        await using (var claim = await f.Sessions.BeginAsync(ct))
        { materialize = (await OutboxStore.ClaimAsync(claim, JobKind.CampaignMaterialize, Guid.NewGuid(), TimeSpan.FromMinutes(1), ct))!; await claim.CommitAsync(ct); }
        if (await new AudienceMaterializer(campaigns).RunAsync(f.Context(0), materialize, ct) != 1) throw new InvalidOperationException("Audience scope failed.");
        JobLease send = (await campaigns.ClaimNoticeAsync(Guid.NewGuid(), ct))!;
        int calls = 0;
        await new BroadcastWorker(campaigns, TimeSpan.FromMilliseconds(100), null, new Dictionary<string, string>()).RunAsync(f.Context(0), send, (notice, text, _) =>
        {
            calls++;
            if (notice.ChatId != CheckoutFixture.Actor(0) || notice.Recipient != "customer_dm" || text != "Public phase message") throw new InvalidOperationException("Private campaign target failed.");
            return Task.FromResult(new NoticeOutcome(NoticeOutcomeKind.Sent, 8765));
        }, ct);
        if (calls != 1 || await campaigns.ClaimNoticeAsync(Guid.NewGuid(), ct) is not null || await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE status='succeeded'", ct) != 1) throw new InvalidOperationException("Known notice replay failed.");
    }
}
