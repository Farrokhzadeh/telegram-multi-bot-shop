namespace Platform.Persistence.Entities;

public sealed class SupportTicket
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid? OrderId { get; set; }
    public string Category { get; set; } = null!;
    public string Status { get; set; } = null!;
    public string Source { get; set; } = null!;
    public Guid? AssignedAdminId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
}
