namespace Platform.Migration;

public sealed record LegacyRecordDisposition(int Archived, int Orders, int Payments, int NativeZero, int Reviewed, IReadOnlyDictionary<string, int> ReviewReasons)
{
    public bool Accounted => Archived == Orders + Payments + NativeZero + Reviewed;
    public bool ReleaseReady => false;
    public override string ToString() => "LegacyRecordDisposition [redacted]";
}
public static class LegacyRecordDispositionReport
{
    public static LegacyRecordDisposition Verify(LegacyWalletOrderPlan plan)
    {
        var originals = new Dictionary<(LegacyProjection Projection, string Id), ArchivedLegacyRecord>();
        foreach (var row in plan.Records)
            if (!originals.TryAdd((row.Source.Projection, row.Source.Id), row)) throw new MigrationRejectedException();
        var selected = new HashSet<(LegacyProjection Projection, string Id)>();
        void Select(LegacyProjection projection, ArchivedLegacyRecord row)
        {
            var key = (projection, row.Source.Id);
            if (row.Source.Projection != projection || !originals.TryGetValue(key, out var original) || original != row || !selected.Add(key)) throw new MigrationRejectedException();
        }
        foreach (var order in plan.Set.Orders) Select(LegacyProjection.OrderRecord, order.Record);
        foreach (var payment in plan.Set.Payments) Select(payment.Record.Source.Projection, payment.Record);
        foreach (var zero in plan.Set.Delivery)
        {
            var key = (LegacyProjection.ZeroTransaction, zero.Source.Id);
            if (zero.Source.Projection != LegacyProjection.ZeroTransaction || !originals.TryGetValue(key, out var original) || original.Source != zero.Source || zero.Target != plan.Set.Orders.SingleOrDefault(o => o.Record.Source.Id == zero.Source.Id && o.NativeZeroSource == zero.Source)?.Record.Target || !selected.Add(key)) throw new MigrationRejectedException();
        }
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var review in plan.Review)
        {
            var key = (review.Projection, review.LegacyId);
            if (!originals.TryGetValue(key, out var original) || original.Target != review.Target || !selected.Add(key) || string.IsNullOrWhiteSpace(review.Reason) || review.Reason.Length > 80 || review.Reason.Any(c => !char.IsAsciiLetterLower(c) && c != '_')) throw new MigrationRejectedException();
            reasons[review.Reason] = reasons.GetValueOrDefault(review.Reason) + 1;
        }
        var result = new LegacyRecordDisposition(originals.Count, plan.Set.Orders.Count, plan.Set.Payments.Count, plan.Set.Delivery.Count, plan.Review.Count, new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(reasons));
        if (selected.Count != originals.Count || !result.Accounted) throw new MigrationRejectedException();
        return result;
    }
}
