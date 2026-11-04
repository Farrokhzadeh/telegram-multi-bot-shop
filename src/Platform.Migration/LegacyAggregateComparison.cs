namespace Platform.Migration;
public sealed record LegacyAggregateExpectations(Guid Batch, string ManifestDigest, long PaidUnfulfilledOrders, long HeldCouponUnits, long TargetCiphertexts)
{
    public override string ToString() => "LegacyAggregateExpectations [redacted]";
}
public sealed record LegacyAggregateReconciliation(Guid Batch, bool WalletMatched, long PaidObligationDifference, long HeldCouponDifference, long CiphertextDifference, long SaleableInventory)
{
    public bool Matched => WalletMatched && PaidObligationDifference == 0 && HeldCouponDifference == 0 && CiphertextDifference == 0 && SaleableInventory == 0;
    public bool ReleaseReady => false;
    public string Scope => "manifest_bound_aggregate_comparison";
    public override string ToString() => "LegacyAggregateReconciliation [redacted]";
}
public static class LegacyAggregateComparison
{
    public static LegacyAggregateReconciliation Compare(VerifiedImportManifest manifest, LegacyAggregateExpectations expected, MigrationReconciliation actual)
    {
        if (expected.Batch != manifest.Batch || actual.Batch != manifest.Batch || expected.ManifestDigest != Convert.ToHexStringLower(manifest.Digest.Span) ||
            expected.PaidUnfulfilledOrders < 0 || expected.HeldCouponUnits < 0 || expected.TargetCiphertexts < 0 ||
            actual.PaidUnfulfilledOrders < 0 || actual.HeldCouponUnits < 0 || actual.TargetCiphertexts < 0 || actual.SaleableInventory < 0) throw new MigrationRejectedException();
        return new(manifest.Batch, actual.Complete, actual.PaidUnfulfilledOrders - expected.PaidUnfulfilledOrders,
            actual.HeldCouponUnits - expected.HeldCouponUnits, actual.TargetCiphertexts - expected.TargetCiphertexts, actual.SaleableInventory);
    }
}
