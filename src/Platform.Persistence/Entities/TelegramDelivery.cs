namespace Platform.Persistence.Entities;

public sealed class TelegramDelivery
{
    public Guid Id { get; set; }
    public Guid BrandId { get; set; }
    public string RecipientKind { get; set; } = null!;
    public Guid? UserId { get; set; }
    public long? StaffChatId { get; set; }
    public string Purpose { get; set; } = null!;
    public string Action { get; set; } = null!;
    public long? TargetMessageId { get; set; }
    public int PartNo { get; set; }
    public Guid? DeliveryGeneration { get; set; }
    public string? RecipientLanguage { get; set; }
    public DateTime? EligibilityCheckedAt { get; set; }
    public string DedupeKey { get; set; } = null!;
    public string Status { get; set; } = null!;
    public long Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public long? TelegramMessageId { get; set; }
    public string? LastErrorCode { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid? CampaignId { get; set; }
    public Guid? CampaignBatchId { get; set; }
    public Guid? DiscountId { get; set; }
    public Guid? OrderId { get; set; }
    public Guid? FulfillmentId { get; set; }
    public Guid? SupportMessageId { get; set; }
    public Guid? ProductStockSubscriptionId { get; set; }
    public Guid? SupplierRequestId { get; set; }
    public Guid? AuditEventId { get; set; }
}
