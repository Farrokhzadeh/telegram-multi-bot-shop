namespace Platform.Persistence.Entities;

public sealed class Job
{
    public Guid Id { get; set; }
    public Guid? BrandId { get; set; }
    public string JobKind { get; set; } = null!;
    public Guid? PaymentId { get; set; }
    public Guid? OrderId { get; set; }
    public Guid? FulfillmentId { get; set; }
    public Guid? CampaignId { get; set; }
    public Guid? DiscountId { get; set; }
    public Guid? SupportMessageId { get; set; }
    public Guid? TelegramDeliveryId { get; set; }
    public Guid? ProductStockSubscriptionId { get; set; }
    public Guid? SupplierRequestId { get; set; }
    public Guid? ProductId { get; set; }
    public Guid? ProductSupplierSourceId { get; set; }
    public Guid? AuditEventId { get; set; }
    public long? OriginRevision { get; set; }
    public string Status { get; set; } = null!;
    public long Attempts { get; set; }
    public DateTime AvailableAt { get; set; }
    public string? LockedBy { get; set; }
    public DateTime? LeaseUntil { get; set; }
    public string DedupeKey { get; set; } = null!;
    public string? LastErrorCode { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
