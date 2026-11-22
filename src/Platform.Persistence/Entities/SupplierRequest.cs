namespace Platform.Persistence.Entities;

public sealed class SupplierRequest
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public int RequestedQuantity { get; set; }
    public Guid? ProductSupplierSourceId { get; set; }
    public long? SourceRevision { get; set; }
    public string? ProviderCode { get; set; }
    public string? AccountRef { get; set; }
    public string? ExternalProductId { get; set; }
    public string? ExternalVariantId { get; set; }
    public string LocalIntentKey { get; set; } = null!;
    public string? RemoteRef { get; set; }
    public byte[] RequestSha256 { get; set; } = null!;
    public string Status { get; set; } = null!;
    public long Revision { get; set; }
    public string? ReviewCode { get; set; }
    public DateTime? NextCheckAt { get; set; }
    public string? EvidenceRef { get; set; }
    public decimal? CostBaselineAmount { get; set; }
    public string? CostCurrency { get; set; }
    public string? CostQuoteRef { get; set; }
    public DateTime? CostObservedAt { get; set; }
    public decimal? ApprovedCostCeiling { get; set; }
    public Guid? ApprovalAuditId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
