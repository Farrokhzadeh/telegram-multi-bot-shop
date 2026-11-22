namespace Platform.Persistence.Entities;

public sealed class Campaign
{
    public Guid Id { get; set; }
    public Guid BatchId { get; set; }
    public Guid BrandId { get; set; }
    public string Language { get; set; } = null!;
    public string AudienceKind { get; set; } = null!;
    public DateTime? ActiveSince { get; set; }
    public string? AudienceLanguage { get; set; }
    public string? MessageText { get; set; }
    public string? MediaRef { get; set; }
    public string Status { get; set; } = null!;
    public Guid CreatedByAdminId { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
