namespace Platform.Persistence.Entities;

public sealed class User
{
    public Guid Id { get; set; }
    public Guid BrandId { get; set; }
    public long TelegramId { get; set; }
    public string? Username { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string Language { get; set; } = null!;
    public DateTime? BlockedAt { get; set; }
    public DateTime? BannedAt { get; set; }
    public DateTime? MarketingOptOutAt { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public string FlowState { get; set; } = null!;
    public long FlowRevision { get; set; }
    public Guid? FlowOrderId { get; set; }
    public Guid? FlowProductId { get; set; }
    public long? FlowAnchorMessageId { get; set; }
    public DateTime? FlowExpiresAt { get; set; }
}
