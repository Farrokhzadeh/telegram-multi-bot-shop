namespace Platform.Persistence.Entities;

public sealed class SupportMessage
{
    public Guid Id { get; set; }
    public Guid TicketId { get; set; }
    public string SenderKind { get; set; } = null!;
    public Guid? SenderAdminId { get; set; }
    public long? SenderTelegramId { get; set; }
    public byte[]? BodyCiphertext { get; set; }
    public byte[]? BodyContent { get; set; }
    public string? BodyKeyId { get; set; }
    public string? AssetRef { get; set; }
    public long? TelegramMessageId { get; set; }
    public DateTime CreatedAt { get; set; }
}
