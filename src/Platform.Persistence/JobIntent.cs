namespace Platform.Persistence;

public enum JobKind
{
    ExpireOrderPayment, VerifyPayment, SettleWalletTopup, SubmitSupplier, ReconcileSupplier, ExportSupplier,
    PrepareManifest, IssuePrepaidCoupon, SendTelegram, DrainLocalPreorders, EvaluateRestock, RefreshCatalogSource,
    RefreshFx, CampaignMaterialize, CouponNotice, SupportNotice, ActivityCleanup, SubscriptionCleanup
}

internal enum JobOrigin { None, Order, Payment, Supplier, Telegram, Product, Source, Campaign, Discount, Support, Brand }

/// <summary>Trusted service intent, not a customer DTO. Database guards verify actual entity/brand/audit ownership.</summary>
public sealed record JobIntent
{
    public JobKind Kind { get; }
    public Guid? BrandId { get; }
    public Guid? OriginId { get; }
    public Guid? AuditId { get; }
    public long? OriginRevision { get; }
    public string Code => Describe(Kind).Code;
    public bool RequiresExplicitRecovery => Kind is JobKind.SubmitSupplier or JobKind.ReconcileSupplier or JobKind.ExportSupplier or JobKind.SendTelegram;
    internal JobOrigin Origin => Describe(Kind).Origin;

    private JobIntent(JobKind kind, JobOrigin origin, Guid? brand, Guid? entity, Guid? audit = null, long? revision = null)
    {
        var expected = Describe(kind);
        if (expected.Origin != origin || (expected.BrandRequired && (brand is null || brand == Guid.Empty)) ||
            (!expected.BrandRequired && brand is not null) ||
            (origin is not (JobOrigin.None or JobOrigin.Brand) && (entity is null || entity == Guid.Empty)) ||
            (origin is (JobOrigin.None or JobOrigin.Brand) && entity is not null) ||
            (audit.HasValue != revision.HasValue) || audit == Guid.Empty || revision < 0 ||
            (origin != JobOrigin.Supplier && audit is not null))
            throw new ArgumentException("Invalid typed job intent.");
        Kind = kind; BrandId = brand; OriginId = entity; AuditId = audit; OriginRevision = revision;
    }

    public static JobIntent Order(JobKind kind, Guid brand, Guid order) => new(kind, JobOrigin.Order, brand, order);
    public static JobIntent Payment(JobKind kind, Guid brand, Guid payment) => new(kind, JobOrigin.Payment, brand, payment);
    public static JobIntent Supplier(JobKind kind, Guid brand, Guid request, Guid? audit = null, long? revision = null) => new(kind, JobOrigin.Supplier, brand, request, audit, revision);
    public static JobIntent Telegram(Guid brand, Guid delivery) => new(JobKind.SendTelegram, JobOrigin.Telegram, brand, delivery);
    public static JobIntent Product(JobKind kind, Guid product) => new(kind, JobOrigin.Product, null, product);
    public static JobIntent CatalogSource(Guid source) => new(JobKind.RefreshCatalogSource, JobOrigin.Source, null, source);
    public static JobIntent Campaign(Guid brand, Guid campaign) => new(JobKind.CampaignMaterialize, JobOrigin.Campaign, brand, campaign);
    public static JobIntent Coupon(Guid brand, Guid discount) => new(JobKind.CouponNotice, JobOrigin.Discount, brand, discount);
    public static JobIntent Support(Guid brand, Guid message) => new(JobKind.SupportNotice, JobOrigin.Support, brand, message);
    public static JobIntent BrandFx(Guid brand) => new(JobKind.RefreshFx, JobOrigin.Brand, brand, null);
    public static JobIntent Maintenance(JobKind kind) => new(kind, JobOrigin.None, null, null);

    internal static string CodeOf(JobKind kind) => Describe(kind).Code;

    internal static JobIntent Read(string code, Guid? brand, Guid? origin, Guid? audit, long? revision)
    {
        foreach (var kind in Enum.GetValues<JobKind>())
        {
            var description = Describe(kind);
            if (description.Code == code) return new(kind, description.Origin, brand, origin, audit, revision);
        }
        throw new FormatException("Unknown job kind.");
    }

    private static (string Code, JobOrigin Origin, bool BrandRequired) Describe(JobKind kind) => kind switch
    {
        JobKind.ExpireOrderPayment => ("expire_order_payment", JobOrigin.Order, true),
        JobKind.VerifyPayment => ("verify_payment", JobOrigin.Payment, true),
        JobKind.SettleWalletTopup => ("settle_wallet_topup", JobOrigin.Payment, true),
        JobKind.SubmitSupplier => ("submit_supplier", JobOrigin.Supplier, true),
        JobKind.ReconcileSupplier => ("reconcile_supplier", JobOrigin.Supplier, true),
        JobKind.ExportSupplier => ("export_supplier", JobOrigin.Supplier, true),
        JobKind.PrepareManifest => ("prepare_manifest", JobOrigin.Order, true),
        JobKind.IssuePrepaidCoupon => ("issue_prepaid_coupon", JobOrigin.Order, true),
        JobKind.SendTelegram => ("send_telegram", JobOrigin.Telegram, true),
        JobKind.DrainLocalPreorders => ("drain_local_preorders", JobOrigin.Product, false),
        JobKind.EvaluateRestock => ("evaluate_restock", JobOrigin.Product, false),
        JobKind.RefreshCatalogSource => ("refresh_catalog_source", JobOrigin.Source, false),
        JobKind.RefreshFx => ("refresh_fx", JobOrigin.Brand, true),
        JobKind.CampaignMaterialize => ("campaign_materialize", JobOrigin.Campaign, true),
        JobKind.CouponNotice => ("coupon_notice", JobOrigin.Discount, true),
        JobKind.SupportNotice => ("support_notice", JobOrigin.Support, true),
        JobKind.ActivityCleanup => ("activity_cleanup", JobOrigin.None, false),
        JobKind.SubscriptionCleanup => ("subscription_cleanup", JobOrigin.None, false),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
