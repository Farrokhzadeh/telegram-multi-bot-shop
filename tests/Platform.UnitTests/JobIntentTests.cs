using Platform.Persistence;

namespace Platform.UnitTests;

public sealed class JobIntentTests
{
    [Fact]
    public void EveryKindHasOneTypedOriginAndExplicitRecoveryPolicy()
    {
        Guid brand = Guid.NewGuid(), origin = Guid.NewGuid();
        foreach (var kind in Enum.GetValues<JobKind>())
        {
            JobIntent intent = kind switch
            {
                JobKind.ExpireOrderPayment or JobKind.PrepareManifest or JobKind.IssuePrepaidCoupon => JobIntent.Order(kind, brand, origin),
                JobKind.VerifyPayment or JobKind.SettleWalletTopup => JobIntent.Payment(kind, brand, origin),
                JobKind.SubmitSupplier or JobKind.ReconcileSupplier or JobKind.ExportSupplier => JobIntent.Supplier(kind, brand, origin),
                JobKind.SendTelegram => JobIntent.Telegram(brand, origin),
                JobKind.CampaignMaterialize => JobIntent.Campaign(brand, origin),
                JobKind.CouponNotice => JobIntent.Coupon(brand, origin),
                JobKind.SupportNotice => JobIntent.Support(brand, origin),
                JobKind.DrainLocalPreorders or JobKind.EvaluateRestock => JobIntent.Product(kind, origin),
                JobKind.RefreshCatalogSource => JobIntent.CatalogSource(origin),
                JobKind.RefreshFx => JobIntent.BrandFx(brand),
                _ => JobIntent.Maintenance(kind)
            };
            Assert.Equal(kind, intent.Kind);
            Assert.NotEmpty(intent.Code);
            if (kind is JobKind.DrainLocalPreorders or JobKind.EvaluateRestock or JobKind.RefreshCatalogSource or JobKind.ActivityCleanup or JobKind.SubscriptionCleanup)
                Assert.Null(intent.BrandId);
            else Assert.Equal(brand, intent.BrandId);
            Assert.Equal(kind is JobKind.SubmitSupplier or JobKind.ReconcileSupplier or JobKind.ExportSupplier or JobKind.SendTelegram,
                intent.RequiresExplicitRecovery);
        }
    }

    [Fact]
    public void ExtraScopeEmptyIdentitiesAndWrongOriginCategoriesAreRejected()
    {
        Guid brand = Guid.NewGuid(), origin = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => JobIntent.Order(JobKind.VerifyPayment, brand, origin));
        Assert.Throws<ArgumentException>(() => JobIntent.Payment(JobKind.SubmitSupplier, brand, origin));
        Assert.Throws<ArgumentException>(() => JobIntent.Maintenance(JobKind.RefreshFx));
        Assert.Throws<ArgumentException>(() => JobIntent.Product(JobKind.RefreshCatalogSource, origin));
        Assert.Throws<ArgumentException>(() => JobIntent.Telegram(Guid.Empty, origin));
        Assert.Throws<ArgumentException>(() => JobIntent.CatalogSource(Guid.Empty));
        Assert.Throws<ArgumentException>(() => JobIntent.Supplier(JobKind.SubmitSupplier, brand, origin, Guid.NewGuid(), null));
        Assert.Throws<ArgumentException>(() => JobIntent.Supplier(JobKind.SubmitSupplier, brand, origin, null, 1));
        Assert.Throws<ArgumentException>(() => JobIntent.Supplier(JobKind.SubmitSupplier, brand, origin, Guid.Empty, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => JobIntent.Maintenance((JobKind)999));
    }
}
