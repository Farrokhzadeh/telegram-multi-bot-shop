using System.Security.Cryptography;
using System.Text;
using Platform.Security;

namespace Platform.Persistence;

public sealed record CampaignDraft(Guid Id, Guid BrandId, string Language, string Audience, DateTime? ActiveSince, string? AudienceLanguage, string? Text, string? Media);
public sealed partial class CampaignStore(DbSessionFactory sessions)
{
    public async Task<Guid> CreateAsync(AuthenticatedAdmin actor, Guid batch, CampaignDraft[] variants, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(variants);
        if (batch == Guid.Empty || variants.Length is < 1 or > 100 || variants.Any(v => v is null || v.Id == Guid.Empty || v.BrandId == Guid.Empty || v.Language is not ("en" or "fa") ||
            v.Audience is not ("all" or "active_since" or "language") || ((v.Audience == "active_since") != v.ActiveSince.HasValue) || ((v.Audience == "language") != (v.AudienceLanguage is not null)) ||
            v.ActiveSince?.Kind is DateTimeKind.Local or DateTimeKind.Unspecified || (v.Text is null && v.Media is null) || v.Text?.Length > 4096 || (v.Text is not null && string.IsNullOrWhiteSpace(v.Text)) ||
            (v.Media is not null && (!v.Media.StartsWith("protected:", StringComparison.Ordinal) || v.Media.Length > 1024)) || (v.AudienceLanguage is not null && v.AudienceLanguage != v.Language)) ||
            variants.Select(v => v.Id).Distinct().Count() != variants.Length || variants.Select(v => (v.BrandId, v.Language)).Distinct().Count() != variants.Length) throw new ArgumentException("Invalid campaign launch.");
        var frozen = variants.OrderBy(v => v.Id).ToArray();
        byte[] hash = AdminCommand.Hash("campaign-launch-v1", frozen.SelectMany(v => new[] { v.Id.ToString("D"), v.BrandId.ToString("D"), v.Language, v.Audience, v.ActiveSince?.ToString("O"), v.AudienceLanguage, v.Text, v.Media }).Prepend(batch.ToString("D")).ToArray());
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
            await session.ExecuteAsync(new DbStatement("SELECT pg_advisory_xact_lock($1)", DbValue.Bigint(BitConverter.ToInt64(batch.ToByteArray(), 0))), ct);
            var current = await session.QueryManyAsync(new DbStatement("SELECT id FROM shop.campaigns WHERE batch_id=$1 ORDER BY id", DbValue.Uuid(batch)), r => r.GetGuid(0), 100, ct);
            if (current.Count != 0 && !current.SequenceEqual(frozen.Select(v => v.Id))) throw new InvalidOperationException("Campaign launch identity conflict.");
            foreach (var variant in frozen)
            {
                if (!await AdminAuditStore.AppendAsync(session, actor, new(variant.Id, AuditAction.Create, AuditEntity.Campaign, variant.Id, variant.BrandId, AuditReason.OperatorReview, batch, hash), ct)) continue;
                await session.ExecuteAsync(new DbStatement("""
                    INSERT INTO shop.campaigns(id,batch_id,brand_id,language,audience_kind,active_since,audience_language,message_text,media_ref,created_by_admin_id)
                    VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)
                    """, DbValue.Uuid(variant.Id), DbValue.Uuid(batch), DbValue.Uuid(variant.BrandId), DbValue.Text(variant.Language), DbValue.Text(variant.Audience), AdminCommand.Time(variant.ActiveSince), AdminCommand.Text(variant.AudienceLanguage), AdminCommand.Text(variant.Text), AdminCommand.Text(variant.Media), DbValue.Uuid(actor.AdminId)), ct);
            }
            await session.CommitAsync(ct); return batch;
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
    public async Task TransitionAsync(AuthenticatedAdmin actor, Guid batch, bool cancel, Guid command, string reason, CancellationToken ct = default)
    {
        AdminCommand.Reason(reason);
        if (command == Guid.Empty) throw new ArgumentException("Command identity is required.");
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
            await session.ExecuteAsync(new DbStatement("SELECT pg_advisory_xact_lock($1)", DbValue.Bigint(BitConverter.ToInt64(batch.ToByteArray(), 0))), ct);
            var rows = await session.QueryManyAsync(new DbStatement("SELECT id,brand_id,status FROM shop.campaigns WHERE batch_id=$1 ORDER BY id FOR UPDATE", DbValue.Uuid(batch)), r => (Id: r.GetGuid(0), Brand: r.GetGuid(1), Status: r.GetString(2)), 100, ct);
            if (rows.Count == 0) throw new InvalidOperationException("Campaign batch is unavailable.");
            foreach (var row in rows)
            {
                Guid audit = AuditId(command, row.Id);
                if (!await AdminAuditStore.AppendAsync(session, actor, new(audit, cancel ? AuditAction.Disable : AuditAction.CampaignApprove, AuditEntity.Campaign, row.Id, row.Brand,
                    AuditReason.OperatorReview, command, AdminCommand.Hash("campaign-transition-v1", batch.ToString("D"), cancel.ToString(), reason)), ct)) continue;
                if (row.Status is "completed" or "cancelled") throw new InvalidOperationException("Campaign batch is terminal.");
                if (cancel)
                {
                    await session.ExecuteAsync(new DbStatement("UPDATE shop.campaigns SET status='cancelled',completed_at=clock_timestamp() WHERE id=$1", DbValue.Uuid(row.Id)), ct);
                    await session.ExecuteAsync(new DbStatement("UPDATE shop.telegram_deliveries SET status='cancelled' WHERE campaign_id=$1 AND status IN ('pending','retry')", DbValue.Uuid(row.Id)), ct);
                    await session.ExecuteAsync(new DbStatement("UPDATE shop.jobs SET status='cancelled',completed_at=clock_timestamp() WHERE status IN ('pending','retry') AND (campaign_id=$1 OR telegram_delivery_id IN (SELECT id FROM shop.telegram_deliveries WHERE campaign_id=$1 AND status='cancelled'))", DbValue.Uuid(row.Id)), ct);
                }
                else
                {
                    await session.ExecuteAsync(new DbStatement("UPDATE shop.campaigns SET status='approved',approved_at=COALESCE(approved_at,clock_timestamp()) WHERE id=$1 AND status='draft'", DbValue.Uuid(row.Id)), ct);
                    await OutboxStore.EnqueueAsync(session, JobIntent.Campaign(row.Brand, row.Id), row.Id, DateTime.UtcNow, ct);
                }
            }
            await session.CommitAsync(ct);
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
    public async Task<Guid> StaffNoticeAsync(AuthenticatedAdmin actor, BrandContext context, Guid request, Guid audit, Guid generation, bool bonus, long trustedChat, CancellationToken ct = default)
    {
        if (trustedChat >= 0 || generation == Guid.Empty) throw new ArgumentException("A trusted private staff destination is required.");
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
            bool valid = await session.QuerySingleAsync(new DbStatement("""
                SELECT EXISTS(SELECT 1 FROM shop.supplier_requests r JOIN shop.orders o ON o.id=r.order_id JOIN shop.users u ON u.id=o.user_id
                    JOIN shop.audit_events a ON a.id=$2 WHERE r.id=$1 AND u.brand_id=$3 AND a.brand_id=$3 AND a.actor_kind='admin'
                    AND ((a.entity_kind='supplier_request' AND a.entity_id=r.id) OR (a.entity_kind='order' AND a.entity_id=o.id)))
                    AND (NOT $4 OR EXISTS(SELECT 1 FROM shop.inventory_items WHERE supplier_request_id=$1 AND origin_kind='supplier_bonus' AND state='admin_held'))
                """, DbValue.Uuid(request), DbValue.Uuid(audit), DbValue.Uuid(context.BrandId), DbValue.Boolean(bonus)), r => r.GetBoolean(0), ct);
            if (!valid) throw new UnauthorizedAccessException("Staff notice origin is unavailable.");
            string purpose = bonus ? "staff_supplier_bonus" : "staff_supplier_case";
            string dedupe = purpose + ":" + AuditId(audit, generation).ToString("D") + ":" + request.ToString("D");
            Guid id = await session.QuerySingleAsync(new DbStatement("""
                INSERT INTO shop.telegram_deliveries(id,brand_id,recipient_kind,staff_chat_id,purpose,action,dedupe_key,supplier_request_id,audit_event_id,delivery_generation)
                VALUES ($1,$2,'staff_chat',$3,$4,'send',$5,$6,$7,$8) ON CONFLICT(dedupe_key) DO UPDATE SET dedupe_key=EXCLUDED.dedupe_key RETURNING id
                """, DbValue.Uuid(Guid.CreateVersion7()), DbValue.Uuid(context.BrandId), DbValue.Bigint(trustedChat), DbValue.Text(purpose), DbValue.Text(dedupe), DbValue.Uuid(request), DbValue.Uuid(audit), DbValue.Uuid(generation)), r => r.GetGuid(0), ct);
            await OutboxStore.EnqueueAsync(session, JobIntent.Telegram(context.BrandId, id), id, DateTime.UtcNow, ct);
            await session.CommitAsync(ct); return id;
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
    private static Guid AuditId(Guid command, Guid entity) => new(SHA256.HashData(Encoding.ASCII.GetBytes(command.ToString("D") + ":" + entity.ToString("D"))).AsSpan(0, 16));
}
