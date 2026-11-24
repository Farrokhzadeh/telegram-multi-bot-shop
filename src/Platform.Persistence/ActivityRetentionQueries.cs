using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace Platform.Persistence;

internal static class ActivityRetentionQueries
{
    internal static async Task<bool> CoveredAsync(EfSession data, Entities.BotActivityEvent value, CancellationToken ct)
    {
        var owner = await (from u in data.Data.Set<Entities.User>() join b in data.Data.Set<Entities.Brand>() on u.BrandId equals b.Id where u.Id == value.UserId select new { u.FlowRevision, u.BrandId, b.TelegramUpdateHighwater, b.TelegramUpdateWindow }).SingleOrDefaultAsync(ct);
        if (owner is null) return false;
        if (value.TelegramUpdateId is long update && update >= 0 && owner.TelegramUpdateHighwater >= update)
        {
            if (owner.TelegramUpdateHighwater - update >= 1024) return true;
            int offset = (int)(owner.TelegramUpdateHighwater - update); if ((owner.TelegramUpdateWindow[offset / 8] & (1 << (offset % 8))) != 0) return true;
        }
        if (value.ActionCode is "flow_expired" or "flow_abandoned" && value.FlowRevision is long revision && owner.FlowRevision > revision) return true;
        if (value.ActionCode is "checkout_created" or "checkout_funded" or "checkout_expired" or "checkout_replaced")
        {
            bool matched = await data.Data.Set<Entities.Order>().AnyAsync(o => o.Id == value.OrderId && o.UserId == value.UserId && o.ProductId == value.ProductId && o.Quantity == value.QuantitySelected && (value.ActionCode == "checkout_created" && o.CreatedAt == value.OccurredAt || value.ActionCode == "checkout_funded" && (o.PaymentStatus == "paid" || o.PaymentStatus == "not_required") && o.PaymentConfirmedAt == value.OccurredAt || value.ActionCode == "checkout_expired" && o.ResolutionKind == "unpaid_expiry" && o.ResolutionAt == value.OccurredAt || value.ActionCode == "checkout_replaced" && o.ResolutionKind == "replaced" && o.ResolutionAt == value.OccurredAt), ct);
            if (matched) return true;
        }
        bool noLinks = value.ProductId == null && value.OrderId == null && value.SupportTicketId == null && value.QuantitySelected == null && value.FlowRevision == null && value.TelegramUpdateId == null;
        if (value.ActionCode is "quantity_enter" or "checkout_rejected" && value.SourceKind == "flow" && value.OutcomeCode == (value.ActionCode == "quantity_enter" ? "accepted" : "rejected") && value.ProductId != null && value.QuantitySelected != null && value.OrderId == null && value.SupportTicketId == null && value.FlowRevision == null && value.TelegramUpdateId == null && Workflow(value.IdempotencyKey, value.ActionCode, out _)) return true;
        if (value.ActionCode == "policy_check" && value.SourceKind == "system" && value.OutcomeCode is "accepted" or "blocked" && noLinks && Workflow(value.IdempotencyKey, value.ActionCode, out _)) return true;
        if (value.ActionCode == "discounts_open" && value.SourceKind == "flow" && value.OutcomeCode is "accepted" or "rejected" && noLinks && value.IdempotencyKey.Length == "coupon_lookup:".Length + 32 && Regex.IsMatch(value.IdempotencyKey, "^coupon_lookup:[0-9a-f]{32}$", RegexOptions.CultureInvariant)) return true;
        if (value.EventType != "payment" || value.SourceKind != "system" || value.SupportTicketId != null || value.FlowRevision != null || value.TelegramUpdateId != null || !Workflow(value.IdempotencyKey, value.ActionCode, out Guid origin)) return false;
        Guid payment = origin;
        if (value.ActionCode == "payment_denied")
        {
            Guid? id = await data.Data.Set<Entities.AuditEvent>().Where(a => a.Id == origin && a.EntityKind == "payment" && a.BrandId == owner.BrandId && a.ActorKind == "admin" && a.Action == "update" && a.CreatedAt == value.OccurredAt).Select(a => a.EntityId).SingleOrDefaultAsync(ct);
            if (id is not Guid found) return false; payment = found;
        }
        return await (from p in data.Data.Set<Entities.Payment>() join o in data.Data.Set<Entities.Order>() on p.OrderId equals o.Id into orders from o in orders.DefaultIfEmpty() where p.Id == payment && (p.TopupUserId ?? (o == null ? null : (Guid?)o.UserId)) == value.UserId && (o == null ? null : o.ProductId) == value.ProductId && (o == null ? null : (Guid?)o.Id) == value.OrderId && (o == null ? null : (int?)o.Quantity) == value.QuantitySelected && (value.ActionCode == "payment_denied" || value.ActionCode == "topup_created" && p.TopupUserId != null && p.CreatedAt == value.OccurredAt || value.ActionCode == "payment_awaiting" && p.ProviderPaymentRef != null || value.ActionCode == "payment_observed" && p.FirstPaymentObservedAt == value.OccurredAt || value.ActionCode == "payment_evidence_ready" && p.ConfirmedAt == value.OccurredAt || value.ActionCode == "payment_review" && p.Status == "review" || value.ActionCode == "payment_settled" && p.Status == "settled" && p.ConfirmedAt == value.OccurredAt) select p.Id).AnyAsync(ct);
    }
    private static bool Workflow(string key, string action, out Guid id)
    {
        string prefix = "workflow:" + action + ":"; id = Guid.Empty;
        return key.StartsWith(prefix, StringComparison.Ordinal) && key.Length == prefix.Length + 32 && Regex.IsMatch(key[prefix.Length..], "^[0-9a-f]{32}$", RegexOptions.CultureInvariant) && Guid.TryParseExact(key[prefix.Length..], "N", out id);
    }
}
internal sealed partial class EfSession
{
    internal async Task<bool> LockActivityAsync(Guid id, CancellationToken ct)
    {
        if (Integrity.RowLock != EfRowLock.None) throw Denied("Row locks cannot overlap.");
        try { Integrity.RowLock = EfRowLock.UpdateSkipLocked; return await Data.Set<Entities.BotActivityEvent>().Where(row => row.Id == id).Select(row => (Guid?)row.Id).FirstOrDefaultAsync(ct) != null; }
        catch { Integrity.Reject(); throw; }
        finally { Integrity.RowLock = EfRowLock.None; }
    }
}
