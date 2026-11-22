namespace Platform.Persistence.Entities;

public sealed class InventoryItem
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public byte[] CredentialCiphertext { get; set; } = null!;
    public string CredentialKeyId { get; set; } = null!;
    public byte[] SourceFingerprint { get; set; } = null!;
    public string OriginKind { get; set; } = null!;
    public Guid? SupplierRequestId { get; set; }
    public string? RemoteItemRef { get; set; }
    public byte[]? SupplierItemKey { get; set; }
    public string State { get; set; } = null!;
    public Guid? AssignedFulfillmentId { get; set; }
    public DateTime? AllocatedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}
