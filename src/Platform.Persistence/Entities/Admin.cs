namespace Platform.Persistence.Entities;

public sealed class Admin
{
    public Guid Id { get; set; }
    public string Login { get; set; } = null!;
    public string AuthenticationMode { get; set; } = null!;
    public string? PasswordVerifier { get; set; }
    public string? ExternalSubject { get; set; }
    public DateTime? DisabledAt { get; set; }
    public long AuthVersion { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public long? TelegramUserId { get; set; }
    public string? PublicContactHandle { get; set; }
    public Guid? TelegramVerifiedByAdminId { get; set; }
    public DateTime? TelegramVerifiedAt { get; set; }
    public Guid? TelegramVerificationAuditId { get; set; }
    public byte[]? BindingChallengeHash { get; set; }
    public long? BindingCandidateTelegramId { get; set; }
    public DateTime? BindingExpiresAt { get; set; }
}
