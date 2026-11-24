using Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace Platform.Persistence;

public sealed partial class CampaignStore
{
    private static async Task<bool> AvailableAsync(DbSession session, Guid brand, Guid product, IReadOnlyDictionary<string, string> accounts, CancellationToken ct)
    {
        var state = await session.RunEfStepAsync(false, async (data, token) =>
        {
            DateTime fresh = (await data.ClockAsync(data.Data.Set<Entities.Product>().Where(row => row.Id == product), token)).AddSeconds(-30);
            return await (from p in data.Data.Set<Entities.Product>() join bp in data.Data.Set<Entities.BrandProduct>() on p.Id equals bp.ProductId join b in data.Data.Set<Entities.Brand>() on bp.BrandId equals b.Id join source in data.Data.Set<Entities.ProductSupplierSource>() on p.ActiveSupplierSourceId equals source.Id into sources from source in sources.DefaultIfEmpty() where p.Id == product && b.Id == brand select new { Published = p.Active && b.Active && bp.Visible, Kind = p.FulfillmentKind, Minimum = bp.MinQuantity, p.LocalFirst, Local = data.Data.Set<Entities.Order>().Any(o => o.ProductId == p.Id && o.IsPreorder && (o.PaymentStatus == "paid" || o.PaymentStatus == "not_required") && (o.FulfillmentStatus == "waiting_stock" || o.FulfillmentStatus == "requires_admin")) ? 0L : data.Data.Set<Entities.InventoryItem>().LongCount(i => i.ProductId == p.Id && i.State == "available"), Provider = source == null ? null : source.ProviderCode, Account = source == null ? null : source.ObservedAccountRef, Remote = source == null ? 0 : source.AvailableQuantity ?? 0, Fresh = source != null && source.Enabled && source.AvailabilityCode == "available" && source.EligibilityCode == "ready_item" && source.ObservedSourceRevision == source.Revision && source.ObservedAt >= fresh }).SingleAsync(token);
        }, ct);
        if (!state.Published) return false;
        if (state.Kind == "manual") return true;
        long local = state.Kind == "supplier" && !state.LocalFirst ? 0 : state.Local;
        if (local >= state.Minimum) return true;
        return state.Kind == "supplier" && state.Fresh && state.Provider is string provider && accounts.TryGetValue(provider, out string? account) && account == state.Account && local + state.Remote >= state.Minimum;
    }
    public async Task<int> EvaluateRestockAsync(JobLease lease, IReadOnlyDictionary<string, string> accounts, CancellationToken ct = default)
    {
        if (lease.Intent.Kind != JobKind.EvaluateRestock || lease.Intent.OriginId is not Guid product) throw new UnauthorizedAccessException();
        var brands = new List<Guid>();
        await using (var read = await sessions.BeginAsync(ct)) brands.AddRange(await read.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.BrandProduct>().Where(row => row.ProductId == product).OrderBy(row => row.BrandId).Select(row => row.BrandId).Take(1000).ToListAsync(token), ct));
        int total = 0;
        bool more = false;
        foreach (Guid brand in brands)
        {
            await using var session = await sessions.BeginAsync(ct);
            try
            {
                IReadOnlyList<(Guid Consent, Guid Member)>? locked = null;
                if (session.IsWriterScoped)
                {
                    locked = await ReadRestockMembersAsync(session, product, brand, ct);
                    foreach (Guid member in locked.Select(v => v.Member).Distinct().Order()) await session.RunEfStepAsync(false, (data, token) => data.LockNoticeMemberAsync(member, token), ct);
                    await session.RunEfStepAsync(false, (data, token) => data.LockProductAsync(product, token), ct);
                    await session.AdmitSharedWriterAsync(ct);
                }
                await OutboxStore.RenewAsync(session, lease, TimeSpan.FromMinutes(5), ct);
                if (!await AvailableAsync(session, brand, product, accounts, ct)) { await session.CommitAsync(ct); continue; }
                var members = locked ?? await ReadRestockMembersAsync(session, product, brand, ct);
                foreach (var item in members)
                {
                    await session.RunEfStepAsync(false, (data, token) => data.LockNoticeMemberAsync(item.Member, token), ct);
                    Guid delivery = Guid.CreateVersion7();
                    bool active = await session.RunEfStepAsync(true, async (data, token) =>
                    {
                        await data.RequireJobCommandAsync(brand, token); await data.LockNoticeSubscriptionAsync(item.Consent, token);
                        var consent = await data.Data.Set<Entities.ProductStockSubscription>().SingleAsync(row => row.Id == item.Consent, token);
                        if (consent.ClosedAt != null || consent.CancelledAt != null || consent.TriggeredAt != null) return false;
                        string language = (await data.Data.Set<Entities.User>().Where(row => row.Id == item.Member).Select(row => row.Language).SingleAsync(token)).ToLowerInvariant().Split('-')[0];
                        DateTime now = await data.BrandClockAsync(brand, token);
                        data.Data.Add(new Entities.TelegramDelivery { Id = delivery, BrandId = brand, RecipientKind = "customer_dm", UserId = item.Member, Purpose = "restock_notice", Action = "send", RecipientLanguage = language is "fa" or "en" ? language : "en", EligibilityCheckedAt = now, DedupeKey = "restock:" + item.Consent.ToString("D"), ProductStockSubscriptionId = item.Consent }); consent.TriggeredAt = now; return true;
                    }, ct);
                    if (!active) continue;
                    await OutboxStore.EnqueueAsync(session, JobIntent.Telegram(brand, delivery), delivery, DateTime.UtcNow, ct);
                    total++;
                }
                more |= members.Count == 1000;
                await session.CommitAsync(ct);
            }
            catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
        }
        await using var finish = await sessions.BeginAsync(ct);
        if (more) await OutboxStore.RetryAsync(finish, lease, TimeSpan.FromSeconds(1), 1000000, JobError.Transient, ct);
        else await OutboxStore.CompleteAsync(finish, lease, ct);
        await finish.CommitAsync(ct); return total;
    }
    private static async Task<IReadOnlyList<(Guid Consent, Guid Member)>> ReadRestockMembersAsync(DbSession session, Guid product, Guid brand, CancellationToken ct)
    {
        var rows = await session.RunEfStepAsync(false, (data, token) => (from s in data.Data.Set<Entities.ProductStockSubscription>() join u in data.Data.Set<Entities.User>() on s.UserId equals u.Id where s.ProductId == product && u.BrandId == brand && s.ClosedAt == null && s.CancelledAt == null && s.TriggeredAt == null orderby s.UserId, s.Id select new { Consent = s.Id, Member = s.UserId }).Take(1000).ToListAsync(token), ct);
        return rows.Select(row => (row.Consent, row.Member)).ToArray();
    }
    public async Task<int> CleanupClosedSubscriptionsAsync(JobLease lease, CancellationToken ct = default)
    {
        if (lease.Intent.Kind != JobKind.SubscriptionCleanup) throw new UnauthorizedAccessException();
        await using var session = await sessions.BeginAsync(ct);
        int count = await CleanupClosedSubscriptionsAsync(session, lease, ct);
        await session.CommitAsync(ct); return count;
    }
    public async Task<int> CleanupClosedSubscriptionsAsync(DbSession session, JobLease lease, CancellationToken ct)
    {
        if (lease.Intent.Kind != JobKind.SubscriptionCleanup) throw new UnauthorizedAccessException();
        try
        {
            await OutboxStore.RenewAsync(session, lease, TimeSpan.FromMinutes(5), ct);
            Guid[] rows = await session.RunEfStepAsync(true, (data, token) => data.CleanupNoticeSubscriptionsAsync(session.IsWriterScoped, token), ct);
            await OutboxStore.CompleteAsync(session, lease, ct); return rows.Length;
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
}
