namespace Platform.Persistence.Entities;

public sealed class BotActivityEvent
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string EventType { get; set; } = null!;
    public string SourceKind { get; set; } = null!;
    public string ActionCode { get; set; } = null!;
    public Guid? ProductId { get; set; }
    public Guid? OrderId { get; set; }
    public Guid? SupportTicketId { get; set; }
    public int? QuantitySelected { get; set; }
    public long? FlowRevision { get; set; }
    public long? TelegramUpdateId { get; set; }
    public string OutcomeCode { get; set; } = null!;
    public string IdempotencyKey { get; set; } = null!;
    public DateTime OccurredAt { get; set; }
}
