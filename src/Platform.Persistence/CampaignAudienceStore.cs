using Platform.Security;

namespace Platform.Persistence;

public sealed partial class CampaignStore
{
    private const string Audience = """
        FROM shop.campaigns c JOIN shop.users u ON u.brand_id=c.brand_id JOIN shop.brands b ON b.id=c.brand_id
        WHERE c.id=$1 AND u.blocked_at IS NULL AND u.banned_at IS NULL AND u.marketing_opt_out_at IS NULL AND u.first_seen_at<=c.approved_at
            AND CASE WHEN split_part(lower(u.language),'-',1) IN ('en','fa') THEN split_part(lower(u.language),'-',1) ELSE split_part(lower(b.default_language),'-',1) END=c.language
            AND (c.audience_kind<>'active_since' OR u.last_seen_at>=c.active_since)
            AND (c.audience_kind<>'language' OR split_part(lower(u.language),'-',1)=c.audience_language)
            AND NOT EXISTS(SELECT 1 FROM shop.telegram_deliveries d WHERE d.campaign_batch_id=c.batch_id AND d.user_id=u.id)
        """;
    public async Task<int> MaterializeAsync(BrandContext context, JobLease lease, CancellationToken ct = default)
    {
        if (lease.Intent.Kind != JobKind.CampaignMaterialize || lease.Intent.BrandId != context.BrandId || lease.Intent.OriginId is not Guid campaign) throw new UnauthorizedAccessException();
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            if (!session.IsWriterScoped) await OutboxStore.RenewAsync(session, lease, TimeSpan.FromMinutes(5), ct);
            var state = await session.QuerySingleAsync(new DbStatement("SELECT c.status,b.active FROM shop.campaigns c JOIN shop.brands b ON b.id=c.brand_id WHERE c.id=$1 AND b.id=$2 AND b.bot_id=$3 FOR UPDATE OF c",
                DbValue.Uuid(campaign), DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId)), r => (Status: r.GetString(0), Active: r.GetBoolean(1)), ct);
            if (state.Status is "cancelled" or "completed") { await OutboxStore.CompleteAsync(session, lease, ct); await session.CommitAsync(ct); return 0; }
            if (state.Status is not ("approved" or "running")) throw new InvalidOperationException("Campaign is not approved.");
            if (!state.Active) { await OutboxStore.RetryAsync(session, lease, TimeSpan.FromMinutes(1), 1000000, JobError.Transient, ct); await session.CommitAsync(ct); return 0; }
            Guid[]? locked = null;
            if (session.IsWriterScoped)
            {
                locked = (await session.QueryManyAsync(new DbStatement("SELECT u.id " + Audience + " ORDER BY u.id LIMIT 1000 FOR UPDATE OF u", DbValue.Uuid(campaign)), r => r.GetGuid(0), 1000, ct)).ToArray();
                await session.AdmitWriterAsync(context, ct);
                await OutboxStore.RenewAsync(session, lease, TimeSpan.FromMinutes(5), ct);
            }
            string scope = locked is null ? "" : locked.Length == 0 ? " AND false " : " AND u.id IN (" + string.Join(",", Enumerable.Range(2, locked.Length).Select(i => "$" + i.ToString(System.Globalization.CultureInfo.InvariantCulture))) + ") ";
            DbValue[] args = locked is null ? [DbValue.Uuid(campaign)] : [DbValue.Uuid(campaign), .. locked.Select(DbValue.Uuid)];
            var rows = await session.QueryManyAsync(new DbStatement("""
                INSERT INTO shop.telegram_deliveries(id,brand_id,recipient_kind,user_id,purpose,action,recipient_language,eligibility_checked_at,dedupe_key,campaign_id,campaign_batch_id)
                SELECT gen_random_uuid(),c.brand_id,'customer_dm',u.id,'campaign','send',c.language,clock_timestamp(),'campaign:'||c.batch_id::text||':'||u.id::text,c.id,c.batch_id
                """ + " " + Audience + scope + " " + """
                ORDER BY u.id LIMIT 1000 ON CONFLICT DO NOTHING RETURNING id
                """, args), r => r.GetGuid(0), 1000, ct);
            foreach (Guid delivery in rows) await OutboxStore.EnqueueAsync(session, JobIntent.Telegram(context.BrandId, delivery), delivery, DateTime.UtcNow, ct);
            await session.ExecuteAsync(new DbStatement("UPDATE shop.campaigns SET status='running',started_at=COALESCE(started_at,clock_timestamp()) WHERE id=$1", DbValue.Uuid(campaign)), ct);
            bool remaining = await session.QuerySingleAsync(new DbStatement("SELECT EXISTS(SELECT 1 " + Audience + ")", DbValue.Uuid(campaign)), r => r.GetBoolean(0), ct);
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
    internal static Task CompleteCampaignAsync(DbSession session, Guid campaign, CancellationToken ct) => session.ExecuteAsync(new DbStatement("""
        UPDATE shop.campaigns SET status='completed',completed_at=COALESCE(completed_at,clock_timestamp()) WHERE id=$1 AND status='running'
            AND NOT EXISTS(SELECT 1 FROM shop.jobs WHERE campaign_id=$1 AND status IN ('pending','leased','retry'))
            AND NOT EXISTS(SELECT 1 FROM shop.telegram_deliveries WHERE campaign_id=$1 AND status IN ('pending','leased','retry','uncertain'))
        """, DbValue.Uuid(campaign)), ct);
}
