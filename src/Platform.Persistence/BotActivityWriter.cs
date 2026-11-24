using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Configurations;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

internal static class BotActivityWriter
{
    internal static async Task CheckoutAsync(DbSession session, BrandContext context, long actor, Guid order, CancellationToken ct)
    {
        var original = await session.RunEfStepAsync(false, (data, token) => (from o in data.Data.Set<Entities.Order>() join u in data.Data.Set<Entities.User>() on o.UserId equals u.Id join b in data.Data.Set<Entities.Brand>() on u.BrandId equals b.Id where o.Id == order && u.BrandId == context.BrandId && u.TelegramId == actor && b.BotId == context.BotId && b.Active select new { Member = u.Id, Product = o.ProductId!.Value, o.Quantity, At = o.CreatedAt }).SingleAsync(token), ct);
        await RecordAsync(session, context, actor, original.Member, BotActivityAction.CheckoutCreated, BotActivitySource.Flow, BotActivityOutcome.Accepted,
            new(original.Product, order, Quantity: original.Quantity), order, original.At, ct);
    }
    internal static async Task<bool> RecordAsync(DbSession session, BrandContext context, long actor, Guid member, BotActivityAction action,
        BotActivitySource source, BotActivityOutcome outcome, BotActivityContext links, Guid identity, DateTime originalAt, CancellationToken ct)
    {
        BotActivityRegistry.Validate(action, source, outcome, links);
        if (identity == Guid.Empty || originalAt.Kind != DateTimeKind.Utc) throw new ArgumentException("Invalid original activity identity.");
        originalAt = new DateTime(originalAt.Ticks - originalAt.Ticks % 10, DateTimeKind.Utc);
        var owner = await BrandUserStore.RequireAsync(session, context, actor, member, ct);
        if (owner.Blocked || owner.Banned) throw new UnauthorizedAccessException("Activity origin is unavailable.");
        return await session.RunEfStepAsync(true, async (data, token) =>
        {
            await data.RequireMembershipAsync(context, actor, false, false, token);
            var clock = await data.Data.Set<Entities.Brand>().Where(row => row.Id == context.BrandId).Select(row => new { Now = EfValues.UtcNow(), AsOf = EfValues.TransactionTime() }).SingleAsync(token);
            if (originalAt > clock.Now) data.RejectArgument("Activity occurrence is in the future.");
            if (originalAt < clock.AsOf.AddHours(-4320)) return false;
            var definition = BotActivityRegistry.Get(action); string sourceCode = BotActivityRegistry.Source(source), outcomeCode = BotActivityRegistry.Outcome(outcome);
            string key = links.TelegramUpdate is long telegram ? BotActivityRegistry.TelegramIdentity(telegram) : "workflow:" + definition.Code + ":" + identity.ToString("N");
            var existing = await data.Data.Set<Entities.BotActivityEvent>().SingleOrDefaultAsync(row => row.UserId == member && row.IdempotencyKey == key, token);
            if (existing is not null)
            {
                if (existing.EventType != definition.EventType || existing.SourceKind != sourceCode || existing.ActionCode != definition.Code || existing.ProductId != links.Product || existing.OrderId != links.Order || existing.SupportTicketId != links.Ticket || existing.QuantitySelected != links.Quantity || existing.FlowRevision != links.FlowRevision || existing.TelegramUpdateId != links.TelegramUpdate || existing.OutcomeCode != outcomeCode || existing.OccurredAt != originalAt) data.RejectConflict("Activity identity content conflicts."); return false;
            }
            data.Data.Add(new Entities.BotActivityEvent { Id = Guid.NewGuid(), UserId = member, EventType = definition.EventType, SourceKind = sourceCode, ActionCode = definition.Code, ProductId = links.Product, OrderId = links.Order, SupportTicketId = links.Ticket, QuantitySelected = links.Quantity, FlowRevision = links.FlowRevision, TelegramUpdateId = links.TelegramUpdate, OutcomeCode = outcomeCode, IdempotencyKey = key, OccurredAt = originalAt }); return true;
        }, ct);
    }
    internal static async Task FlowLeftAsync(DbSession session, BrandContext context, long actor, BrandUser before, BrandUser after, CancellationToken ct)
    {
        if (before.Flow.State is CustomerFlowState.Idle or CustomerFlowState.Catalog ||
            (before.Flow.State == after.Flow.State && before.Flow.ProductId == after.Flow.ProductId && before.Flow.OrderId == after.Flow.OrderId)) return;
        DateTime now = await session.RunEfStepAsync(false, (data, token) => data.BrandClockAsync(context.BrandId, token), ct);
        bool expired = before.Flow.ExpiresAt is DateTime at && at < now;
        await RecordAsync(session, context, actor, before.Id, expired ? BotActivityAction.FlowExpired : BotActivityAction.FlowAbandoned, BotActivitySource.System, BotActivityOutcome.Cancelled,
            new(before.Flow.ProductId, before.Flow.OrderId, FlowRevision: before.Flow.Revision), CustomerFlowExpiryStore.Identity(before.Id, before.Flow.Revision), expired ? before.Flow.ExpiresAt!.Value : now, ct);
    }
    internal static async Task CheckoutRejectedAsync(DbSession session, BrandContext context, long actor, Guid product, int quantity, CancellationToken ct)
    {
        await session.RunEfStepAsync(false, (data, token) => data.LockMemberAsync(context.BrandId, actor, token), ct);
        BrandUser member = await BrandUserStore.RequireAsync(session, context, actor, null, ct);
        if (member.Blocked || member.Banned) return;
        var origin = await session.RunEfStepAsync(false, async (data, token) => (Allowed: await (from l in data.Data.Set<Entities.BrandProduct>() join p in data.Data.Set<Entities.Product>() on l.ProductId equals p.Id where l.BrandId == context.BrandId && l.ProductId == product && l.Visible && p.Active select p.Id).AnyAsync(token), Now: await data.BrandClockAsync(context.BrandId, token)), ct);
        if (origin.Allowed) await BotActivityWriter.RecordAsync(session, context, actor, member.Id, BotActivityAction.CheckoutRejected, BotActivitySource.Flow, BotActivityOutcome.Rejected,
            new(product, Quantity: quantity), Guid.NewGuid(), origin.Now, ct);
    }
}
