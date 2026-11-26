namespace Platform.Persistence.Entities;

public sealed class Discount
{
    public Guid Id { get; set; }
    public Guid BrandId { get; set; }
    public byte[] CodeDigest { get; set; } = null!;
    public byte[] CodeCiphertext { get; set; } = null!;
    public byte[] CodeContent { get; set; } = null!;
    public string CodeKeyId { get; set; } = null!;
    public string DisplayPrefix { get; set; } = null!;
    public Guid IssuanceBatchId { get; set; }
    public string Kind { get; set; } = null!;
    public decimal PercentOff { get; set; }
    public Guid? ProductId { get; set; }
    public Guid? EligibleUserId { get; set; }
    public Guid? SourceOrderId { get; set; }
    public long? MaxTotalUses { get; set; }
    public long? MaxUsesPerUser { get; set; }
    public long RedeemedCount { get; set; }
    public long UsageAdjustmentUnits { get; set; }
    public DateTime? StartsAt { get; set; }
    public DateTime? EndsAt { get; set; }
    public bool Active { get; set; } = true;
    public long Revision { get; set; }
    public Guid? CreatedByAdminId { get; set; }
    public DateTime CreatedAt { get; set; }
}
