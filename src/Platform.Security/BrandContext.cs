using System.Security.Claims;

namespace Platform.Security;

/// <summary>Immutable scope resolved from a server-owned authenticated bot registration, never a customer DTO.</summary>
public sealed class BrandContext
{
    internal BrandContext(BotBrandBinding binding)
    {
        BrandId = binding.BrandId;
        BotId = binding.BotId;
        DeploymentId = binding.DeploymentId;
        ConfigurationRevision = binding.ConfigurationRevision;
    }

    public Guid BrandId { get; }
    public long BotId { get; }
    public Guid DeploymentId { get; }
    public long ConfigurationRevision { get; }

    public void RequireBrand(Guid resourceBrandId)
    {
        if (resourceBrandId != BrandId)
        {
            throw new UnauthorizedAccessException("Resource is outside the authenticated brand scope.");
        }
    }
}

/// <summary>Trusted configuration. Subject is an opaque authentication identity, never a token or customer ID.</summary>
public sealed record BotBrandBinding(string Scheme, string Subject, Guid BrandId, long BotId,
    Guid DeploymentId, long ConfigurationRevision, bool Enabled);

/// <summary>
/// Immutable registration snapshot. Call only with the scheme and principal from a successful server
/// authentication ticket. Credential verification and configuration reload belong to the host's auth/config layers.
/// </summary>
public sealed class BotBrandRegistry
{
    private readonly Dictionary<(string Scheme, string Subject), BrandContext> registrations = [];

    public BotBrandRegistry(IEnumerable<BotBrandBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var bots = new HashSet<long>();
        var brands = new HashSet<Guid>();
        var identities = new HashSet<(string, string)>();
        foreach (var binding in bindings)
        {
            if (binding is null || !IsIdentity(binding.Scheme) || !IsIdentity(binding.Subject) ||
                binding.BrandId == Guid.Empty || binding.BotId <= 0 || binding.DeploymentId == Guid.Empty ||
                binding.ConfigurationRevision <= 0 || !identities.Add((binding.Scheme, binding.Subject)) ||
                !bots.Add(binding.BotId) || !brands.Add(binding.BrandId))
            {
                throw new ArgumentException("Invalid or ambiguous bot brand registration.", nameof(bindings));
            }

            if (binding.Enabled)
            {
                registrations.Add((binding.Scheme, binding.Subject), new BrandContext(binding));
            }
        }
    }

    public bool TryResolveAuthenticated(string scheme, ClaimsPrincipal principal, out BrandContext? context)
    {
        context = null;
        ArgumentNullException.ThrowIfNull(principal);
        var identities = principal.Identities.ToArray();
        if (identities.Length != 1 || !identities[0].IsAuthenticated ||
            !string.Equals(identities[0].AuthenticationType, scheme, StringComparison.Ordinal))
        {
            return false;
        }

        var subjects = identities[0].FindAll(ClaimTypes.NameIdentifier).ToArray();
        if (subjects.Length != 1 || !IsIdentity(subjects[0].Value) ||
            identities[0].Claims.Any(claim => BrandSelector.IsReservedName(claim.Type)))
        {
            return false;
        }

        return registrations.TryGetValue((scheme, subjects[0].Value), out context);
    }

    private static bool IsIdentity(string value) => !string.IsNullOrEmpty(value) && value.Length <= 160 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':' or '/');
}

/// <summary>Reserved customer transport selectors; punctuation/case aliases have the same meaning.</summary>
public static class BrandSelector
{
    public static bool IsReservedName(string name, bool header = false)
    {
        var normalized = name.Replace("-", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
        if (header && normalized.StartsWith('x')) normalized = normalized[1..];
        return normalized is "brand" or "brandid" or "tenant" or "tenantid" or "bot" or "botid" or "deployment" or "deploymentid";
    }
}
