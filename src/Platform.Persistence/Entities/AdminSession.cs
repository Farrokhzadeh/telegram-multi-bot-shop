namespace Platform.Persistence.Entities;

public sealed class AdminSession
{
    public Guid Id { get; set; }
    public Guid AdminId { get; set; }
    public byte[] TokenHash { get; set; } = null!;
    public long AuthVersionAtIssue { get; set; }
    public DateTime IssuedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
}
