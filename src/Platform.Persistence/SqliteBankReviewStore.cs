using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Platform.Security;

namespace Platform.Persistence;

public enum SqliteBankDecisionOutcome { ConfirmedPaid, DeniedUnverified, Replayed }

public sealed class SqliteBankReviewStore(SqliteSessionFactory sessions)
{
    private readonly SqliteSessionFactory sessions = sessions;

    public Task<SqliteBankDecisionOutcome> DecideAsync(BrandContext context, AuthenticatedAdmin actor, Guid payment,
        bool approve, long expectedOrderRevision, Guid command, string reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(actor);
        if (payment == Guid.Empty || command == Guid.Empty || expectedOrderRevision < 0 ||
            string.IsNullOrWhiteSpace(reason) || reason.Length > 1024 || reason.Any(char.IsControl))
            throw new ArgumentException("Invalid protected bank decision.");
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"receipt-decision-v1:{payment:D}:{approve}:{expectedOrderRevision}:{reason}"));
        var audit = new AuditCommand(command, AuditAction.Update, AuditEntity.Payment, payment,
            context.BrandId, AuditReason.OperatorReview, command, hash);
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            if (!await AdminAuditStore.AppendSqliteAsync(tx, actor, audit, token)) return SqliteBankDecisionOutcome.Replayed;
            State state = await ReadAsync(tx, context, payment, token);
            if (state.Method != "bank" || state.Status != "observed" ||
                state.OrderStatus != "under_review" || state.Revision != expectedOrderRevision ||
                state.Receipt is null || state.First is null || state.Due is null ||
                state.First > state.Due ||
                state.Held || state.Review || state.OrderKind is not ("product_purchase" or "prepaid_units") ||
                state.OrderKind == "product_purchase" && state.Kind is not ("local" or "manual") ||
                state.Preorder)
                throw new InvalidOperationException("Bank receipt is not eligible for approval.");
            DateTime instant = await SqliteSessionFactory.StepEfAsync(tx, false, (data, cancel) => data.BrandClockAsync(context.BrandId, cancel), token);
            string now = SqliteSchema.UtcText(instant);
            await SqlitePaymentOperations.PaymentAsync(tx, context, payment, row =>
            {
                if (row.Status != "observed" || row.ReceiptAssetRef == null) throw new InvalidOperationException("Bank decision lost its authorized target.");
                row.Status = approve ? "settled" : "rejected"; row.ReviewAdminId = actor.AdminId; row.ReviewedAt = instant; row.ReviewReason = reason;
                if (approve) { row.AmountReceived = row.AmountDue; row.ConfirmedAt = instant; }
            }, token);
            if (!approve) return SqliteBankDecisionOutcome.DeniedUnverified;
            await SqlitePaymentOperations.OrderAsync(tx, state.Order, row =>
            {
                if (row.PaymentStatus != "under_review" || row.Revision != expectedOrderRevision) throw new InvalidOperationException("Bank decision lost its authorized target.");
                row.PaymentStatus = "paid"; row.PaymentConfirmedAt = instant;
            }, instant, token);
            if (state.OrderKind == "prepaid_units")
                await new SqlitePrepaidSourceStore(sessions)
                    .IssueAsync(tx, context, state.Order, token);
            else if (state.Kind == "local")
            {
                bool queued = await SqliteSessionFactory.StepEfAsync(tx, false, (data, cancel) => data.Data.Set<Entities.Order>().AnyAsync(value => value.ProductId == state.Product && value.Id != state.Order && value.IsPreorder && (value.PaymentStatus == "paid" || value.PaymentStatus == "not_required") && (value.FulfillmentStatus == "waiting_stock" || value.FulfillmentStatus == "requires_admin"), cancel), token);
                List<Guid> stock = queued ? [] :
                    await SqliteOrderPaymentSettlementStore.ReadStockAsync(
                    tx, state.Product, state.Quantity, token);
                if (!queued && stock.Count == state.Quantity)
                {
                    await SqliteOrderPaymentSettlementStore.AcquireAsync(
                        tx, context, state.Order, stock, now, token);
                    await FulfillmentAsync(tx, state.Order, "acquiring", null, now, token);
                }
                else await FulfillmentAsync(tx, state.Order, "requires_admin", "stock_shortage", now, token);
            }
            else await FulfillmentAsync(tx, state.Order, "requires_admin", null, now, token);
            await SqlitePaymentOperations.CancelExpiryAsync(tx, context, state.Order, instant, token);
            await StatusAsync(tx, context, state, token);
            return SqliteBankDecisionOutcome.ConfirmedPaid;
        }, ct);
    }

    private static async Task<State> ReadAsync(SqliteTransaction tx, BrandContext context, Guid payment,
        CancellationToken ct)
    {
        var state = await SqliteSessionFactory.StepEfAsync(tx, false, (data, token) =>
            (from value in data.Data.Set<Entities.Payment>()
             join order in data.Data.Set<Entities.Order>() on value.OrderId equals order.Id
             join user in data.Data.Set<Entities.User>() on order.UserId equals user.Id
             join brand in data.Data.Set<Entities.Brand>() on user.BrandId equals brand.Id
             where value.Id == payment && brand.Id == context.BrandId && brand.BotId == context.BotId && brand.Active
             select new State(value.PaymentMethod, value.Status, value.ReceiptAssetRef, value.FirstPaymentObservedAt, order.PaymentDueAt, order.PaymentStatus, order.Revision, order.ProductId!.Value, order.Quantity, order.FulfillmentKindSnapshot, order.IsPreorder, user.Id, order.Id, order.ReviewCode != null, order.HoldAt != null, order.OrderKind)).SingleOrDefaultAsync(token), ct);
        return state ?? throw new UnauthorizedAccessException("Bank payment is unavailable.");
    }
    private static Task<bool> FulfillmentAsync(SqliteTransaction tx, Guid order, string status, string? review, string now, CancellationToken ct) => SqlitePaymentOperations.OrderAsync(tx, order, row => { row.FulfillmentStatus = status; row.ReviewCode = review; }, DateTime.ParseExact(now, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal), ct);
    private static async Task StatusAsync(SqliteTransaction tx, BrandContext context, State state, CancellationToken ct)
    {
        Guid delivery = await SqlitePaymentOperations.StatusAsync(tx, context, state.Order, state.Member, "receipt-status:" + state.Order.ToString("D"), ct);
        await OutboxStore.EnqueueSqliteAsync(tx, JobIntent.Telegram(context.BrandId, delivery), delivery, DateTime.UtcNow, ct);
    }

    private sealed record State(string Method, string Status, string? Receipt, DateTime? First,
        DateTime? Due, string OrderStatus, long Revision, Guid Product, int Quantity, string Kind,
        bool Preorder, Guid Member, Guid Order, bool Review, bool Held, string OrderKind);
}
