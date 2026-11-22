namespace Platform.Persistence.Entities;

public sealed class Product
{
    public Guid Id { get; set; }
    public string? Sku { get; set; }
    public string FulfillmentKind { get; set; } = null!;
    public bool LocalFirst { get; set; } = true;
    public string AvailabilityPolicy { get; set; } = null!;
    public Guid? ActiveSupplierSourceId { get; set; }
    public long Revision { get; set; }
    public string Title { get; set; } = null!;
    public string? Description { get; set; }
    public string? IconRef { get; set; }
    public string? Guidance { get; set; }
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
