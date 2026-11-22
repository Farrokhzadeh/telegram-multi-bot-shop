namespace Platform.Persistence.Entities;

public sealed class SupplierRequestAttempt
{
    public Guid Id { get; set; }
    public Guid SupplierRequestId { get; set; }
    public long AttemptNo { get; set; }
    public string Operation { get; set; } = null!;
    public string Outcome { get; set; } = null!;
    public Guid? ParentSubmitAttemptId { get; set; }
    public Guid? AdminAuthorizationAuditId { get; set; }
    public string? PurchaseIdempotencyKey { get; set; }
    public decimal? MaxChargeAmount { get; set; }
    public int? HttpStatus { get; set; }
    public string? ErrorCode { get; set; }
    public string? RemoteRef { get; set; }
    public string? EvidenceRef { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}
