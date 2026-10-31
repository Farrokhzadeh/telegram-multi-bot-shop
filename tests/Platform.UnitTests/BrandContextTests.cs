using System.Security.Claims;
using Platform.Security;

namespace Platform.UnitTests;

public sealed class BrandContextTests
{
    private static readonly Guid BrandA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid BrandB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid Deployment = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    [Fact]
    public void AuthenticatedServerBindingSuppliesAllContextFields()
    {
        var bindings = new[] { Binding("a", BrandA, 101), Binding("b", BrandB, 102) };
        var registry = new BotBrandRegistry(bindings);
        bindings[0] = Binding("a", BrandB, 103);
        Assert.True(registry.TryResolveAuthenticated("test-bot", Principal("a"), out var a));
        Assert.True(registry.TryResolveAuthenticated("test-bot", Principal("b"), out var b));
        Assert.Equal(BrandA, a!.BrandId);
        Assert.Equal(101, a.BotId);
        Assert.Equal(Deployment, a.DeploymentId);
        Assert.Equal(7, a.ConfigurationRevision);
        Assert.Equal(BrandB, b!.BrandId);
        a.RequireBrand(BrandA);
        Assert.Throws<UnauthorizedAccessException>(() => a.RequireBrand(BrandB));
        Assert.Throws<UnauthorizedAccessException>(() => a.RequireBrand(Guid.Empty));
        Assert.Empty(typeof(BrandContext).GetConstructors());
    }

    [Theory]
    [InlineData("unknown", "test-bot", true)]
    [InlineData("a", "admin", true)]
    [InlineData("a", "TEST-BOT", true)]
    [InlineData("a", "test-bot", false)]
    public void OnlyExactAuthenticatedRegistrationCanResolve(string subject, string scheme, bool authenticated)
    {
        var registry = new BotBrandRegistry([Binding("a", BrandA, 101)]);
        Assert.False(registry.TryResolveAuthenticated(scheme, Principal(subject, authenticated), out var context));
        Assert.Null(context);
    }

    [Fact]
    public void DisabledAndAmbiguousIdentitiesFailClosed()
    {
        var registry = new BotBrandRegistry([Binding("a", BrandA, 101) with { Enabled = false }]);
        Assert.False(registry.TryResolveAuthenticated("test-bot", Principal("a"), out _));
        registry = new BotBrandRegistry([Binding("a", BrandA, 101)]);
        var duplicate = Principal("a");
        ((ClaimsIdentity)duplicate.Identity!).AddClaim(new Claim(ClaimTypes.NameIdentifier, "a"));
        Assert.False(registry.TryResolveAuthenticated("test-bot", duplicate, out _));
        var extra = Principal("a");
        extra.AddIdentity(new ClaimsIdentity());
        Assert.False(registry.TryResolveAuthenticated("test-bot", extra, out _));
        Assert.False(registry.TryResolveAuthenticated("test-bot", new ClaimsPrincipal(), out _));
    }

    [Theory]
    [InlineData("brand_id")]
    [InlineData("brandId")]
    [InlineData("tenant-id")]
    [InlineData("BotId")]
    [InlineData("deployment_id")]
    public void PrincipalCannotOverrideRegisteredScope(string claimType)
    {
        var principal = Principal("a");
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(claimType, BrandB.ToString()));
        var registry = new BotBrandRegistry([Binding("a", BrandA, 101)]);
        Assert.False(registry.TryResolveAuthenticated("test-bot", principal, out _));
    }

    [Fact]
    public void RegistrationsRejectInvalidOrAmbiguousConfigurationWithoutEchoingIt()
    {
        var valid = Binding("a", BrandA, 101);
        BotBrandBinding[] invalid =
        [
            valid with { BrandId = Guid.Empty }, valid with { BotId = 0 },
            valid with { DeploymentId = Guid.Empty }, valid with { ConfigurationRevision = 0 },
            valid with { Scheme = " " }, valid with { Subject = "private-value\n" }
        ];
        foreach (var binding in invalid)
        {
            var error = Assert.Throws<ArgumentException>(() => new BotBrandRegistry([binding]));
            Assert.DoesNotContain("private-value", error.Message);
        }
        Assert.Throws<ArgumentException>(() => new BotBrandRegistry([valid, valid]));
        Assert.Throws<ArgumentException>(() => new BotBrandRegistry([valid, valid with { Subject = "b", BrandId = BrandB }]));
        Assert.Throws<ArgumentException>(() => new BotBrandRegistry([valid, valid with { Subject = "b", BotId = 102 }]));
    }

    private static BotBrandBinding Binding(string subject, Guid brand, long bot) =>
        new("test-bot", subject, brand, bot, Deployment, 7, true);

    private static ClaimsPrincipal Principal(string subject, bool authenticated = true) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, subject)], authenticated ? "test-bot" : null));
}
