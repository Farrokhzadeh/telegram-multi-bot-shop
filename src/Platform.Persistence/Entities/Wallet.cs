namespace Platform.Persistence.Entities;

public sealed class Wallet
{
    public Guid UserId { get; set; }
    public decimal Balance { get; set; }
    public long Version { get; set; }
    public DateTime UpdatedAt { get; set; }
}
