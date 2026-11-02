using System.Collections.Concurrent;
using System.Security.Claims;

namespace Platform.Security;

public sealed record AiScopeBinding(string Subject, BrandContext Brand, long VerifiedActorId, Guid? OrderId, DateTime ExpiresAt)
{
    public override string ToString() => "AiScopeBinding [redacted]";
}
public sealed class AiReadScope
{
    private readonly Func<bool> active;
    internal AiReadScope(AiScopeBinding binding, Func<bool> active) { this.active = active; Brand = binding.Brand; ActorId = binding.VerifiedActorId; OrderId = binding.OrderId; ExpiresAt = binding.ExpiresAt; }
    public BrandContext Brand { get; }
    public long ActorId { get; }
    public Guid? OrderId { get; }
    public DateTime ExpiresAt { get; }
    public bool IsActive => ExpiresAt > DateTime.UtcNow && active();
    public override string ToString() => "AiReadScope [redacted]";
}
public sealed class AiScopeRegistry
{
    public const string Scheme = "ai-service-v1";
    private readonly ConcurrentDictionary<string, AiScopeBinding> bindings = new(StringComparer.Ordinal);
    public AiScopeRegistry(IEnumerable<AiScopeBinding> configured)
    {
        foreach (var binding in configured)
        {
            if (binding.Brand is null || string.IsNullOrWhiteSpace(binding.Subject) || binding.Subject.Length > 256 || binding.Subject.Any(char.IsControl) || binding.VerifiedActorId <= 0 || binding.OrderId == Guid.Empty || binding.ExpiresAt.Kind != DateTimeKind.Utc || binding.ExpiresAt == DateTime.MaxValue || !bindings.TryAdd(binding.Subject, binding)) throw new ArgumentException("Invalid AI service scope configuration.");
        }
    }
    public AiReadScope? Resolve(string scheme, ClaimsPrincipal principal, DateTime now)
    {
        if (scheme != Scheme || now.Kind != DateTimeKind.Utc || principal.Identities.Count() != 1 || principal.Identity?.IsAuthenticated != true || principal.Identity.AuthenticationType != Scheme) return null;
        string[] subjects = principal.FindAll(ClaimTypes.NameIdentifier).Select(c => c.Value).ToArray();
        if (subjects.Length != 1 || !bindings.TryGetValue(subjects[0], out var binding) || binding.ExpiresAt <= now) return null;
        return new(binding, () => bindings.TryGetValue(binding.Subject, out var current) && ReferenceEquals(current, binding));
    }
    public bool Revoke(string subject) => bindings.TryRemove(subject, out _);
}
