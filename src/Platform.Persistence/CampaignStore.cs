using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
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
            var current = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.Campaign>().Where(row => row.BatchId == batch).OrderBy(row => row.Id).Select(row => row.Id).Take(101).ToListAsync(token), ct);
            if (current.Count != 0 && !current.SequenceEqual(frozen.Select(v => v.Id))) throw new InvalidOperationException("Campaign launch identity conflict.");
            await session.AdmitSharedWriterAsync(ct);
            foreach (var variant in frozen)
            {
                if (!await AdminAuditStore.AppendAsync(session, actor, new(variant.Id, AuditAction.Create, AuditEntity.Campaign, variant.Id, variant.BrandId, AuditReason.OperatorReview, batch, hash), ct)) continue;
                await session.RunEfStepAsync(true, async (data, token) => { await data.RequireAdminAsync(actor, null, token); data.Data.Add(new Entities.Campaign { Id = variant.Id, BatchId = batch, BrandId = variant.BrandId, Language = variant.Language, AudienceKind = variant.Audience, ActiveSince = variant.ActiveSince, AudienceLanguage = variant.AudienceLanguage, MessageText = variant.Text, MediaRef = variant.Media, CreatedByAdminId = actor.AdminId }); return true; }, ct);
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
            var rows = await session.RunEfStepAsync(false, async (data, token) => { await data.LockCampaignBatchAsync(batch, token); return await data.Data.Set<Entities.Campaign>().Where(row => row.BatchId == batch).OrderBy(row => row.Id).Select(row => new { row.Id, Brand = row.BrandId, row.Status }).Take(101).ToListAsync(token); }, ct);
            if (rows.Count > 100) throw new InvalidOperationException("Campaign batch bound exceeded.");
            if (rows.Count == 0) throw new InvalidOperationException("Campaign batch is unavailable.");
            await session.AdmitSharedWriterAsync(ct);
            foreach (var row in rows)
            {
                Guid audit = AuditId(command, row.Id);
                if (!await AdminAuditStore.AppendAsync(session, actor, new(audit, cancel ? AuditAction.Disable : AuditAction.CampaignApprove, AuditEntity.Campaign, row.Id, row.Brand,
                    AuditReason.OperatorReview, command, AdminCommand.Hash("campaign-transition-v1", batch.ToString("D"), cancel.ToString(), reason)), ct)) continue;
                if (row.Status is "completed" or "cancelled") throw new InvalidOperationException("Campaign batch is terminal.");
                if (cancel)
                {
                    await session.RunEfStepAsync(true, async (data, token) =>
                    {
                        await data.RequireAdminAsync(actor, null, token); var campaign = await data.Data.Set<Entities.Campaign>().SingleAsync(value => value.Id == row.Id, token); campaign.Status = "cancelled"; campaign.CompletedAt = await data.BrandClockAsync(row.Brand, token); await data.FlushAsync(token, false);
                        while (true) { var deliveries = await data.Data.Set<Entities.TelegramDelivery>().Where(value => value.CampaignId == row.Id && (value.Status == "pending" || value.Status == "retry")).Take(128).ToListAsync(token); if (deliveries.Count == 0) break; foreach (var delivery in deliveries) delivery.Status = "cancelled"; await data.FlushAsync(token, false); }
                        while (true) { var jobs = await data.Data.Set<Entities.Job>().Where(value => (value.Status == "pending" || value.Status == "retry") && (value.CampaignId == row.Id || data.Data.Set<Entities.TelegramDelivery>().Any(delivery => delivery.Id == value.TelegramDeliveryId && delivery.CampaignId == row.Id && delivery.Status == "cancelled"))).Take(128).ToListAsync(token); if (jobs.Count == 0) break; DateTime now = await data.BrandClockAsync(row.Brand, token); foreach (var job in jobs) { job.Status = "cancelled"; job.CompletedAt = now; } await data.FlushAsync(token, false); }
                        return true;
                    }, ct);
                }
                else
                {
                    await session.RunEfStepAsync(true, async (data, token) => { await data.RequireAdminAsync(actor, null, token); var campaign = await data.Data.Set<Entities.Campaign>().SingleAsync(value => value.Id == row.Id, token); if (campaign.Status == "draft") { campaign.Status = "approved"; campaign.ApprovedAt ??= await data.BrandClockAsync(row.Brand, token); } return true; }, ct);
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
            bool valid = await session.RunEfStepAsync(false, (data, token) => (from r in data.Data.Set<Entities.SupplierRequest>() join o in data.Data.Set<Entities.Order>() on r.OrderId equals o.Id join u in data.Data.Set<Entities.User>() on o.UserId equals u.Id from a in data.Data.Set<Entities.AuditEvent>() where r.Id == request && a.Id == audit && u.BrandId == context.BrandId && a.BrandId == context.BrandId && a.ActorKind == "admin" && (a.EntityKind == "supplier_request" && a.EntityId == r.Id || a.EntityKind == "order" && a.EntityId == o.Id) && (!bonus || data.Data.Set<Entities.InventoryItem>().Any(i => i.SupplierRequestId == request && i.OriginKind == "supplier_bonus" && i.State == "admin_held" && i.SupplierItemKey != null && i.RemoteItemRef != null && r.EvidenceRef != null)) select r.Id).AnyAsync(token), ct);
            if (!valid) throw new UnauthorizedAccessException("Staff notice origin is unavailable.");
            await session.AdmitWriterAsync(context, ct);
            string purpose = bonus ? "staff_supplier_bonus" : "staff_supplier_case";
            string dedupe = purpose + ":" + AuditId(audit, generation).ToString("D") + ":" + request.ToString("D");
            Guid id = await session.RunEfStepAsync(true, async (data, token) => { await data.RequireAdminAsync(actor, null, token); var existing = await data.Data.Set<Entities.TelegramDelivery>().Where(row => row.DedupeKey == dedupe).Select(row => (Guid?)row.Id).SingleOrDefaultAsync(token); if (existing is Guid prior) return prior; Guid created = Guid.CreateVersion7(); data.Data.Add(new Entities.TelegramDelivery { Id = created, BrandId = context.BrandId, RecipientKind = "staff_chat", StaffChatId = trustedChat, Purpose = purpose, Action = "send", DedupeKey = dedupe, SupplierRequestId = request, AuditEventId = audit, DeliveryGeneration = generation }); return created; }, ct);
            await OutboxStore.EnqueueAsync(session, JobIntent.Telegram(context.BrandId, id), id, DateTime.UtcNow, ct);
            await session.CommitAsync(ct); return id;
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
    private static Guid AuditId(Guid command, Guid entity) => new(SHA256.HashData(Encoding.ASCII.GetBytes(command.ToString("D") + ":" + entity.ToString("D"))).AsSpan(0, 16));
}
