using Platform.Security;

namespace Platform.UnitTests;

public sealed class AdminAuthTests
{
    [Fact]
    public void PasswordVerifiersUseIndependentSaltsAndRejectWrongOrMalformedCredentials()
    {
        string password = new('s', 20);
        string first = AdminAuth.CreatePasswordVerifier(password);
        string second = AdminAuth.CreatePasswordVerifier(password);
        Assert.NotEqual(first, second);
        Assert.True(AdminAuth.VerifyPassword(password, first));
        Assert.False(AdminAuth.VerifyPassword(new string('x', 20), first));
        Assert.False(AdminAuth.VerifyPassword(password, "unrecognized"));
        Assert.False(AdminAuth.VerifyPassword(password, first.Replace(":600000:", ":1:", StringComparison.Ordinal)));
        Assert.DoesNotContain(password, first);
    }

    [Fact]
    public void SessionSecretsAreOpaqueCanonicalAndDisposable()
    {
        using var token = AdminToken.Create();
        using var parsed = AdminToken.Parse(token.ExportForTransport());
        Assert.Equal(token.Hash(), parsed.Hash());
        Assert.Equal(43, token.ExportForTransport().Length);
        Assert.Equal("Admin secret (redacted)", token.ToString());
        Assert.Throws<AdminAuthenticationException>(() => AdminToken.Parse("private-payload"));
        Assert.Throws<AdminAuthenticationException>(() => AdminToken.Parse(token.ExportForTransport() + "="));
        token.Dispose();
        Assert.Throws<ObjectDisposedException>(() => token.ExportForTransport());
    }

    [Fact]
    public void BindingChallengesAreBoundToAccountVersionCandidateAndChannelStage()
    {
        using var token = AdminToken.Create();
        Guid admin = Guid.NewGuid();
        var first = AdminAuth.BindingHash(token, admin, 1, 123, false);
        Assert.NotEqual(first, AdminAuth.BindingHash(token, admin, 1, 123, true));
        Assert.NotEqual(first, AdminAuth.BindingHash(token, admin, 2, 123, false));
        Assert.NotEqual(first, AdminAuth.BindingHash(token, admin, 1, 124, false));
        Assert.NotEqual(first, AdminAuth.BindingHash(token, Guid.NewGuid(), 1, 123, false));
    }
}
