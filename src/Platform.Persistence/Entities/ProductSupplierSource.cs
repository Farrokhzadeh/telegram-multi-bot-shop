namespace Platform.Persistence.Entities;

public sealed class ProductSupplierSource
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public string ProviderCode { get; set; } = null!;
    public string ExternalProductId { get; set; } = null!;
    public string? ExternalVariantId { get; set; }
    public bool Enabled { get; set; }
    public long Revision { get; set; } = 1L;
    public long ObservationRevision { get; set; }
    public long? ObservedSourceRevision { get; set; }
    public string? ObservedAccountRef { get; set; }
    public DateTime? ObservedAt { get; set; }
    public string AvailabilityCode { get; set; } = null!;
    public int? AvailableQuantity { get; set; }
    public decimal? ObservedUnitCost { get; set; }
    public string? ObservedCurrency { get; set; }
    public string EligibilityCode { get; set; } = null!;
    public string? QuoteRef { get; set; }
    public string? ObservationErrorCode { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
