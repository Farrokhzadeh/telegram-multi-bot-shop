using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class BrandAdministrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task EncryptedTokenAndChannelLifecycleQualifiesNativeCompatibleServices() => await BrandAdministrationQualification.RunAsync(Ct);
    [Fact]
    public async Task StaleChangedAndUnverifiedChannelConfigurationCannotReplaceTheCommittedList()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        var admin = await f.AdminAsync(Ct);
        var store = new BrandAdministrationStore(f.Sessions, f.Secrets);
        var channels = new[] { new RequiredChannel(-10001, "Public channel", "https://t.me/public_channel") };
        BrandPolicySnapshot original;
        await using (var read = await f.Sessions.BeginAsync(Ct)) original = await BrandPolicyStore.ReadAsync(read, f.Context(0), CheckoutFixture.Actor(0), Ct);
        Guid command = Guid.NewGuid();
        Assert.Equal(1, await store.SetChannelsAsync(admin, f.Context(0), channels, 0, command, Eligible, Ct));
        await using (var stale = await f.Sessions.BeginAsync(Ct))
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => BrandPolicyStore.RequireCurrentAsync(stale, f.Context(0), CheckoutFixture.Actor(0), original, true, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetChannelsAsync(admin, f.Context(0), [], 0, Guid.NewGuid(), Eligible, Ct));
        await Assert.ThrowsAsync<FormatException>(() => store.SetChannelsAsync(admin, f.Context(0), [], 0, command, Eligible, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetChannelsAsync(admin, f.Context(0), [], 1, Guid.NewGuid(), (_, _, _) => Task.FromResult(false), Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.brand_channels WHERE active", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.audit_events WHERE entity_kind='brand'", Ct));
        Assert.Equal(2, await store.SetChannelsAsync(admin, f.Context(0), [], 1, Guid.NewGuid(), Eligible, Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.brand_channels WHERE active", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.brand_channels WHERE NOT active", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.brands WHERE id<>'" + f.Brands[0].ToString("D") + "' AND policy_version<>0", Ct));
    }
    [Fact]
    public async Task FailedOrRacingTokenVerificationPreservesOldCredentialAndAuditAtomically()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        var admin = await f.AdminAsync(Ct);
        var store = new BrandAdministrationStore(f.Sessions, f.Secrets);
        using var token = BotToken.Parse("1001:" + new string('s', 35));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.RotateTokenAsync(admin, f.Context(0), token, 0, Guid.NewGuid(), (_, _) => Task.FromResult(new VerifiedBotProfile(1002, "public_wrong_bot")), Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.RotateTokenAsync(admin, f.Context(1), token, 0, Guid.NewGuid(), (_, _) => throw new InvalidOperationException(), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RotateTokenAsync(admin, f.Context(0), token, 0, Guid.NewGuid(), async (_, ct) =>
        {
            await store.SetChannelsAsync(admin, f.Context(0), [], 0, Guid.NewGuid(), Eligible, ct);
            return new(1001, "public_test_bot");
        }, Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.audit_events WHERE reason='credential_rotation'", Ct));
        Assert.Equal(3L, await f.CountAsync("SELECT count(*) FROM shop.brands WHERE octet_length(bot_token_ciphertext)=1", Ct));
    }
    [Fact]
    public async Task VaultRejectsForeignScopeTamperingAndDisposedPlaintext()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        using var token = BotToken.Parse("1001:" + new string('s', 35));
        var vault = new BotTokenVault(f.Secrets);
        var encrypted = vault.Protect(f.Context(0), token);
        Assert.Throws<CipherRejectedException>(() => vault.OpenForTrustedTransport(f.Context(1), encrypted.KeyId, encrypted.Ciphertext));
        encrypted.Ciphertext[^1] ^= 1;
        Assert.Throws<CipherRejectedException>(() => vault.OpenForTrustedTransport(f.Context(0), encrypted.KeyId, encrypted.Ciphertext));
        var previous = token.ForTrustedTransport();
        token.Dispose();
        Assert.True(previous.Span.IndexOfAnyExcept((byte)0) < 0);
        Assert.Throws<ObjectDisposedException>(() => token.ForTrustedTransport());
    }
    [Theory]
    [InlineData("")]
    [InlineData("1001:short")]
    [InlineData("1001:invalid public credential value with spaces")]
    [InlineData("01001:sssssssssssssssssssssssssssssssssss")]
    public void MalformedCredentialsRejectWithSafeErrors(string value)
    {
        var error = Assert.Throws<ArgumentException>(() => BotToken.Parse(value));
        Assert.DoesNotContain(value.Length == 0 ? "Token=" : value, error.Message, StringComparison.Ordinal);
    }
    private static Task<bool> Eligible(BrandContext context, IReadOnlyList<RequiredChannel> channels, CancellationToken ct) => Task.FromResult(true);
}
