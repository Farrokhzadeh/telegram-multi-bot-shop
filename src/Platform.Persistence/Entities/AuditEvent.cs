namespace Platform.Persistence.Entities;

public sealed class AuditEvent
{
    public Guid Id { get; set; }
    public string ActorKind { get; set; } = null!;
    public Guid? ActorAdminId { get; set; }
    public Guid? ActorUserId { get; set; }
    public Guid? BrandId { get; set; }
    public string Action { get; set; } = null!;
    public string EntityKind { get; set; } = null!;
    public Guid? EntityId { get; set; }
    public string Reason { get; set; } = null!;
    public long? PreviousRevision { get; set; }
    public long? NewRevision { get; set; }
    public Guid CorrelationId { get; set; }
    public byte[]? CommandSha256 { get; set; }
    public bool? BeforeChannelEnforcement { get; set; }
    public bool? AfterChannelEnforcement { get; set; }
    public DateTime CreatedAt { get; set; }
}
