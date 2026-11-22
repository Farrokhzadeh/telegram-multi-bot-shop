namespace Platform.Persistence.Entities;

public sealed class Fulfillment
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public int UnitNo { get; set; }
    public string? Kind { get; set; }
    public string Status { get; set; } = null!;
    public Guid? SupplierRequestId { get; set; }
    public string? RemoteItemRef { get; set; }
    public byte[]? SupplierItemKey { get; set; }
    public byte[]? DeliveredContent { get; set; }
    public string? DeliveredContentKeyId { get; set; }
    public string? ContentKind { get; set; }
    public Guid? ResolutionAdminId { get; set; }
    public string? ResolutionNote { get; set; }
    public Guid? ManifestGeneration { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
