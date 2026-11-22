namespace Platform.Persistence.Entities;

public sealed class WalletEntry
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public decimal SignedAmount { get; set; }
    public decimal? BalanceAfter { get; set; }
    public long? PostingVersion { get; set; }
    public string MovementKind { get; set; } = null!;
    public bool IsPosting { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public Guid? OrderId { get; set; }
    public Guid? PaymentId { get; set; }
    public Guid? SupplierRequestId { get; set; }
    public Guid? LegacyImportBatch { get; set; }
    public Guid? AdminId { get; set; }
    public Guid? AuditEventId { get; set; }
    public string Reason { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
}
