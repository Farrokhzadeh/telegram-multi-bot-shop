using Microsoft.Data.Sqlite;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

public sealed class SqlitePrepaidSourceStore(SqliteSessionFactory sessions)
{
    private readonly SqliteSessionFactory sessions = sessions;

    public Task<Guid> IssueAsync(BrandContext context, Guid source, CancellationToken ct = default) => sessions.RunAsync(true, (_, tx, token) => IssueAsync(tx, context, source, token), ct);
    internal async Task<Guid> IssueAsync(SqliteTransaction tx, BrandContext context, Guid source, CancellationToken ct)
    {
        if (source == Guid.Empty) throw new ArgumentException("Prepaid source order is unavailable.");
        var sale = await SqliteSessionFactory.StepEfAsync(tx, false, (data, token) => PrepaidEntitlements.ReadAsync(data, context, source, token), ct);
        if (sale is null || sale.Kind != "prepaid_units" || sale.Status != "paid" || sale.Version != PricingPolicy.Version || sale.Step != PricingPolicy.TomanStep) throw new PricingRejectedException();
        var due = PrepaidEntitlements.Due(sale);
        Guid? prior = await SqliteSessionFactory.StepEfAsync(tx, false, (data, token) => PrepaidEntitlements.PriorAsync(data, source, token), ct);
        if (prior is Guid existing) return existing;
        if (sale.Quantity <= 0 || due.Amount <= 0 || sale.Percent is < 0 or >= 100 || !await SqliteSessionFactory.StepEfAsync(tx, false, (data, token) => PrepaidEntitlements.EvidenceAsync(data, sale, source, due.Amount, token), ct)) throw new PricingRejectedException();
        Guid coupon = await SqliteSessionFactory.StepEfAsync(tx, true, (data, token) => PrepaidEntitlements.CreateAsync(data, context, source, sale, token), ct);
        await SqliteDiscountClaimStore.TransitionAsync(tx, context, source, true, ct);
        await OutboxStore.EnqueueSqliteAsync(tx, JobIntent.Coupon(context.BrandId, coupon), source, DateTime.UtcNow, ct);
        return coupon;
    }
}
