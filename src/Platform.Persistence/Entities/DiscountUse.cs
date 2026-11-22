namespace Platform.Persistence.Entities;

public sealed class DiscountUse
{
    public Guid DiscountId { get; set; }
    public Guid OrderId { get; set; }
    public Guid DiscountRevisionId { get; set; }
    public int UnitsCovered { get; set; }
    public decimal SavedAmount { get; set; }
    public string SavedCurrency { get; set; } = null!;
    public string Status { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime TransitionedAt { get; set; }
}
