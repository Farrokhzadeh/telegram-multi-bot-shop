using Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace Platform.Persistence;

public sealed partial class CampaignStore
{
    private sealed record AudienceMember { public Guid Member { get; init; } public Guid Brand { get; init; } public Guid Batch { get; init; } public string Language { get; init; } = null!; public Guid Campaign { get; init; } }
    private static IQueryable<AudienceMember> Audience(EfSession data, Guid campaign) => from c in data.Data.Set<Entities.Campaign>() join u in data.Data.Set<Entities.User>() on c.BrandId equals u.BrandId join b in data.Data.Set<Entities.Brand>() on c.BrandId equals b.Id let lower = u.Language.ToLower() let language = lower.Contains("-") ? lower.Substring(0, lower.IndexOf("-")) : lower let fallback = b.DefaultLanguage.ToLower() where c.Id == campaign && u.BlockedAt == null && u.BannedAt == null && u.MarketingOptOutAt == null && u.FirstSeenAt <= c.ApprovedAt && (language == "en" || language == "fa" ? language : fallback.Contains("-") ? fallback.Substring(0, fallback.IndexOf("-")) : fallback) == c.Language && (c.AudienceKind != "active_since" || u.LastSeenAt >= c.ActiveSince) && (c.AudienceKind != "language" || language == c.AudienceLanguage) && !data.Data.Set<Entities.TelegramDelivery>().Any(d => d.CampaignBatchId == c.BatchId && d.UserId == u.Id) select new AudienceMember { Member = u.Id, Brand = c.BrandId, Batch = c.BatchId, Language = c.Language, Campaign = c.Id };
    public async Task<int> MaterializeAsync(BrandContext context, JobLease lease, CancellationToken ct = default)
    {
        if (lease.Intent.Kind != JobKind.CampaignMaterialize || lease.Intent.BrandId != context.BrandId || lease.Intent.OriginId is not Guid campaign) throw new UnauthorizedAccessException();
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            if (!session.IsWriterScoped) await OutboxStore.RenewAsync(session, lease, TimeSpan.FromMinutes(5), ct);
            var state = await session.RunEfStepAsync(false, async (data, token) => { await data.LockCampaignAsync(campaign, token); return await (from c in data.Data.Set<Entities.Campaign>() join b in data.Data.Set<Entities.Brand>() on c.BrandId equals b.Id where c.Id == campaign && b.Id == context.BrandId && b.BotId == context.BotId select new { c.Status, b.Active }).SingleAsync(token); }, ct);
            if (state.Status is "cancelled" or "completed") { await OutboxStore.CompleteAsync(session, lease, ct); await session.CommitAsync(ct); return 0; }
            if (state.Status is not ("approved" or "running")) throw new InvalidOperationException("Campaign is not approved.");
            if (!state.Active) { await OutboxStore.RetryAsync(session, lease, TimeSpan.FromMinutes(1), 1000000, JobError.Transient, ct); await session.CommitAsync(ct); return 0; }
            Guid[]? locked = null;
            if (session.IsWriterScoped)
            {
                locked = await session.RunEfStepAsync(false, async (data, token) => { var eligible = Audience(data, campaign).Select(row => row.Member); return await data.LockAudienceMembersAsync(eligible, token); }, ct);
                await session.AdmitWriterAsync(context, ct);
                await OutboxStore.RenewAsync(session, lease, TimeSpan.FromMinutes(5), ct);
            }
            var rows = await session.RunEfStepAsync(true, async (data, token) =>
            {
                await data.RequireJobCommandAsync(context.BrandId, token);
                var query = Audience(data, campaign); if (locked is not null) query = query.Where(row => locked.Contains(row.Member));
                var members = await query.OrderBy(row => row.Member).Take(1000).ToListAsync(token); var ids = new List<Guid>(members.Count);
                DateTime now = await data.BrandClockAsync(context.BrandId, token);
                foreach (var batch in members.Chunk(128))
                {
                    foreach (var member in batch) { Guid id = Guid.NewGuid(); data.Data.Add(new Entities.TelegramDelivery { Id = id, BrandId = member.Brand, RecipientKind = "customer_dm", UserId = member.Member, Purpose = "campaign", Action = "send", RecipientLanguage = member.Language, EligibilityCheckedAt = now, DedupeKey = "campaign:" + member.Batch.ToString("D") + ":" + member.Member.ToString("D"), CampaignId = member.Campaign, CampaignBatchId = member.Batch }); ids.Add(id); }
                    await data.FlushAsync(token, false);
                }
                return ids;
            }, ct);
            foreach (Guid delivery in rows) await OutboxStore.EnqueueAsync(session, JobIntent.Telegram(context.BrandId, delivery), delivery, DateTime.UtcNow, ct);
            await session.RunEfStepAsync(true, async (data, token) => { await data.RequireJobCommandAsync(context.BrandId, token); var row = await data.Data.Set<Entities.Campaign>().SingleAsync(row => row.Id == campaign, token); row.Status = "running"; row.StartedAt ??= await data.BrandClockAsync(context.BrandId, token); return true; }, ct);
            bool remaining = await session.RunEfStepAsync(false, (data, token) => Audience(data, campaign).AnyAsync(token), ct);
            if (remaining) await OutboxStore.RetryAsync(session, lease, TimeSpan.FromSeconds(1), 1000000, JobError.Transient, ct);
            else
            {
                await OutboxStore.CompleteAsync(session, lease, ct);
                await CompleteCampaignAsync(session, campaign, ct);
            }
            await session.CommitAsync(ct); return rows.Count;
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
    internal static async Task CompleteCampaignAsync(DbSession session, Guid campaign, CancellationToken ct) => await session.RunEfStepAsync(true, async (data, token) =>
    {
        var row = await data.Data.Set<Entities.Campaign>().SingleOrDefaultAsync(row => row.Id == campaign && row.Status == "running" && !data.Data.Set<Entities.Job>().Any(job => job.CampaignId == campaign && (job.Status == "pending" || job.Status == "leased" || job.Status == "retry")) && !data.Data.Set<Entities.TelegramDelivery>().Any(d => d.CampaignId == campaign && (d.Status == "pending" || d.Status == "leased" || d.Status == "retry" || d.Status == "uncertain")), token);
        if (row is null) return false; await data.RequireJobCommandAsync(row.BrandId, token); row.Status = "completed"; row.CompletedAt ??= await data.BrandClockAsync(row.BrandId, token); return true;
    }, ct);
}
