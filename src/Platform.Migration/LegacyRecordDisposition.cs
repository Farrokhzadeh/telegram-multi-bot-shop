namespace Platform.Migration;

public sealed record LegacyRecordDisposition(int Archived, int Orders, int Payments, int NativeZero, int Reviewed, IReadOnlyDictionary<string, int> ReviewReasons, int Catalog = 0)
{
    public bool Accounted => Archived == Orders + Payments + NativeZero + Catalog + Reviewed;
    public bool ReleaseReady => false;
    public override string ToString() => "LegacyRecordDisposition [redacted]";
}
public static class LegacyRecordDispositionReport
{
    public static LegacyRecordDisposition Verify(LegacyWalletOrderPlan plan) => Verify(plan.Records, plan.Set.Orders, plan.Set.Payments, plan.Set.Delivery, plan.Review, plan.Set.Catalog);
    public static LegacyRecordDisposition Verify(IReadOnlyList<ArchivedLegacyRecord> records, IReadOnlyList<LegacyHistoricalRecord> orders, IReadOnlyList<LegacyPaymentRecord> payments, IReadOnlyList<ArchivedLegacyRecord> delivery, IReadOnlyList<LegacyRecordReview> review, IReadOnlyList<ArchivedLegacyRecord>? catalog = null)
    {
        var originals = new Dictionary<(LegacyProjection Projection, string Id), ArchivedLegacyRecord>();
        foreach (var row in records)
            if (!originals.TryAdd((row.Source.Projection, row.Source.Id), row)) throw new MigrationRejectedException();
        var selected = new HashSet<(LegacyProjection Projection, string Id)>();
        void Select(LegacyProjection projection, ArchivedLegacyRecord row)
        {
            var key = (projection, row.Source.Id);
            if (row.Source.Projection != projection || !originals.TryGetValue(key, out var original) || original != row || !selected.Add(key)) throw new MigrationRejectedException();
        }
        foreach (var order in orders) Select(LegacyProjection.OrderRecord, order.Record);
        foreach (var payment in payments)
        {
            if (payment.Record.Source.Projection is not (LegacyProjection.PaymentAttempt or LegacyProjection.ManualDeposit)) throw new MigrationRejectedException();
            Select(payment.Record.Source.Projection, payment.Record);
        }
        foreach (var product in catalog ?? []) Select(LegacyProjection.ProductRecord, product);
        foreach (var zero in delivery)
        {
            var key = (LegacyProjection.ZeroTransaction, zero.Source.Id);
            if (zero.Source.Projection != LegacyProjection.ZeroTransaction || !originals.TryGetValue(key, out var original) || original.Source != zero.Source || !orders.Any(o => o.Record.Source.Id == zero.Source.Id && o.NativeZeroSource == zero.Source && o.Record.Target == zero.Target) || !selected.Add(key)) throw new MigrationRejectedException();
        }
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in review)
        {
            var key = (item.Projection, item.LegacyId);
            if (!originals.TryGetValue(key, out var original) || original.Target != item.Target || !selected.Add(key) || string.IsNullOrWhiteSpace(item.Reason) || item.Reason.Length > 80 || item.Reason.Any(c => !char.IsAsciiLetterLower(c) && c != '_')) throw new MigrationRejectedException();
            reasons[item.Reason] = reasons.GetValueOrDefault(item.Reason) + 1;
        }
        var result = new LegacyRecordDisposition(originals.Count, orders.Count, payments.Count, delivery.Count, review.Count, new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(reasons), catalog?.Count ?? 0);
        if (selected.Count != originals.Count || !result.Accounted) throw new MigrationRejectedException();
        return result;
    }
}
