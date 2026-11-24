using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Platform.Security;

namespace Platform.Persistence;

public sealed record SqliteStagingFlow(string State, Guid? Product, Guid? Order, long? Anchor, long Revision);

public sealed class SqliteStagingIngress(SqliteSessionFactory sessions)
{
    public Task<bool> AdmitAsync(BrandContext context, long update, CancellationToken ct = default)
    {
        if (update < 0 || update > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(update));
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            return await SqliteSessionFactory.StepEfAsync(tx, true, async (data, cancel) => { await data.RequireJobCommandAsync(context.BrandId, cancel); var row = await data.Data.Set<Entities.Brand>().SingleOrDefaultAsync(row => row.Id == context.BrandId && row.BotId == context.BotId && row.Active && row.TelegramUpdateHighwater < update, cancel); if (row is null) return false; row.TelegramUpdateHighwater = update; return true; }, token);
        }, ct);
    }

    public Task<long> NextOffsetAsync(BrandContext context, CancellationToken ct = default) =>
        sessions.RunAsync(false, async (_, tx, token) =>
        {
            long? current = await SqliteSessionFactory.StepEfAsync(tx, false, (data, cancel) => data.Data.Set<Entities.Brand>().Where(row => row.Id == context.BrandId && row.BotId == context.BotId && row.Active).Select(row => (long?)row.TelegramUpdateHighwater).SingleOrDefaultAsync(cancel), token);
            return current is long id ? checked(id + 1) : throw new UnauthorizedAccessException("Bot update cursor is unavailable.");
        }, ct);

    public Task<SqliteStagingFlow> ReadFlowAsync(BrandContext context, long actor, CancellationToken ct = default) =>
        sessions.RunAsync(false, async (_, tx, token) => await ReadAsync(tx, context, actor, token), ct);

    public Task<SqliteStagingFlow> UpdateFlowAsync(BrandContext context, long actor, string state, Guid? product, Guid? order,
        long? anchor, long expectedRevision, CancellationToken ct = default)
    {
        if (state is not ("idle" or "catalog" or "quantity" or "payment" or "support") ||
            expectedRevision < 0 || anchor is <= 0 ||
            (state == "payment" && (product is null || order is null || anchor is null)) ||
            (state is ("quantity" or "catalog") && product is null))
            throw new UnauthorizedAccessException("Invalid staged customer flow.");
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            await SqliteSessionFactory.StepEfAsync(tx, true, async (data, cancel) => { await data.RequireJobCommandAsync(context.BrandId, cancel); var row = await data.Data.Set<Entities.User>().SingleOrDefaultAsync(row => row.BrandId == context.BrandId && row.TelegramId == actor && row.FlowRevision == expectedRevision && row.BlockedAt == null && row.BannedAt == null, cancel) ?? throw new UnauthorizedAccessException("Staging flow was superseded."); row.FlowState = state; row.FlowProductId = product; row.FlowOrderId = order; row.FlowAnchorMessageId = anchor; row.FlowRevision++; row.FlowExpiresAt = state == "idle" ? null : DateTime.UtcNow.AddMinutes(15); return true; }, token);
            return await ReadAsync(tx, context, actor, token);
        }, ct);
    }

    private static async Task<SqliteStagingFlow> ReadAsync(SqliteTransaction tx, BrandContext context, long actor,
        CancellationToken ct)
    {
        var row = await SqliteSessionFactory.StepEfAsync(tx, false, (data, token) => (from u in data.Data.Set<Entities.User>() join b in data.Data.Set<Entities.Brand>() on u.BrandId equals b.Id where u.BrandId == context.BrandId && b.BotId == context.BotId && b.Active && u.TelegramId == actor && u.BlockedAt == null && u.BannedAt == null select new { u.FlowState, u.FlowProductId, u.FlowOrderId, u.FlowAnchorMessageId, u.FlowRevision, u.FlowExpiresAt }).SingleOrDefaultAsync(token), ct) ?? throw new UnauthorizedAccessException("Staging customer is unavailable.");
        return new(row.FlowExpiresAt <= DateTime.UtcNow ? "idle" : row.FlowState, row.FlowProductId, row.FlowOrderId, row.FlowAnchorMessageId, row.FlowRevision);
    }
}
