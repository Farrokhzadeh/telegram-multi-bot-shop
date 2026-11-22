namespace Platform.Persistence.Entities;

public sealed class BrandChannel
{
    public Guid BrandId { get; set; }
    public long ChatId { get; set; }
    public string? Title { get; set; }
    public string? InviteLink { get; set; }
    public bool Required { get; set; } = true;
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
