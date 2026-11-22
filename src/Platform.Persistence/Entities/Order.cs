namespace Platform.Persistence.Entities;

public sealed class Order
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid? ProductId { get; set; }
    public int Quantity { get; set; }
    public long Revision { get; set; }
    public string OrderKind { get; set; } = null!;
    public string PaymentStatus { get; set; } = null!;
    public string FulfillmentStatus { get; set; } = null!;
    public string Currency { get; set; } = null!;
    public string PaymentMethod { get; set; } = null!;
    public decimal UnitPriceUsdt { get; set; }
    public decimal? UnitPriceToman { get; set; }
    public decimal? FxRateSnapshot { get; set; }
    public string? FxSourceSnapshot { get; set; }
    public DateTime? FxFetchedAtSnapshot { get; set; }
    public bool AutoPriceTomanSnapshot { get; set; }
    public decimal PricingRoundingStepSnapshot { get; set; }
    public string PriceCalculationVersion { get; set; } = null!;
    public decimal? DiscountPctSnapshot { get; set; }
    public int? DiscountUnitsSnapshot { get; set; }
    public DateTime? PaymentDueAt { get; set; }
    public DateTime? PaymentConfirmedAt { get; set; }
    public string BrandPublicNameSnapshot { get; set; } = null!;
    public string ProductTitleSnapshot { get; set; } = null!;
    public string? ProductDescriptionSnapshot { get; set; }
    public string? ProductIconSnapshot { get; set; }
    public string? ProductGuidanceSnapshot { get; set; }
    public string PaymentTermsSnapshot { get; set; } = null!;
    public string FulfillmentKindSnapshot { get; set; } = null!;
    public bool LocalFirstSnapshot { get; set; }
    public bool IsPreorder { get; set; }
    public string? ReviewCode { get; set; }
    public DateTime? HoldAt { get; set; }
    public Guid? HoldAdminId { get; set; }
    public string? HoldReason { get; set; }
    public string? ResolutionKind { get; set; }
    public Guid? ResolutionAdminId { get; set; }
    public DateTime? ResolutionAt { get; set; }
    public Guid? ResolutionAuditEventId { get; set; }
    public Guid? ResolutionWalletEntryId { get; set; }
    public Guid? DeliveryGeneration { get; set; }
    public string? ManifestAssetRef { get; set; }
    public byte[]? ManifestDigest { get; set; }
    public DateTime? ManifestPreparedAt { get; set; }
    public DateTime? ReleaseAuthorizedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
