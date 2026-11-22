namespace Platform.Persistence.Entities;

public sealed class DiscountRevision
{
    public Guid Id { get; set; }
    public Guid DiscountId { get; set; }
    public long RevisionNo { get; set; }
    public Guid AuditEventId { get; set; }
    public decimal PercentOff { get; set; }
    public long? MaxTotalUses { get; set; }
    public long? MaxUsesPerUser { get; set; }
    public long UsageAdjustmentUnits { get; set; }
    public long RedeemedCountAtRevision { get; set; }
    public long HeldUnitsAtRevision { get; set; }
    public DateTime? StartsAt { get; set; }
    public DateTime? EndsAt { get; set; }
    public bool Active { get; set; }
    public Guid? ProductId { get; set; }
    public Guid? EligibleUserId { get; set; }
}
