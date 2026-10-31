using Platform.Persistence;
using Platform.Security;
using Platform.Telegram;

namespace Platform.IntegrationTests;

public sealed class BrandPolicyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly string Password = new('s', 20);
    private static readonly Lazy<string> Verifier = new(() => AdminAuth.CreatePasswordVerifier(Password));

    [Theory]
    [InlineData(ChannelMembership.Unknown, false)]
    [InlineData(ChannelMembership.Left, false)]
    [InlineData(ChannelMembership.Kicked, false)]
    [InlineData(ChannelMembership.Member, true)]
    [InlineData(ChannelMembership.Administrator, true)]
    [InlineData(ChannelMembership.Creator, true)]
    [InlineData(ChannelMembership.RestrictedMember, true)]
    public async Task OrdinaryCustomerMustJoinEveryActiveRequiredChannel(ChannelMembership status, bool accepted)
    {
        await using var fixture = await BrandFixture.CreateAsync(Ct);
        await Channels(fixture);
        BrandPolicySnapshot policy = await Read(fixture, 0);
        int calls = 0;
        ChannelAccess? access = await BrandPolicy.CheckChannelsAsync(policy, 7001, (chat, actor, _) =>
        {
            Assert.Equal(-1001, chat);
            Assert.Equal(7001, actor);
            calls++;
            return Task.FromResult(status);
        }, Ct);
        Assert.Equal(1, calls);
        Assert.Equal(accepted, access is not null);
        if (access is null) return;
        await using (var authorize = await fixture.Sessions.BeginAsync(Ct))
        {
            await access.RequireCurrentAsync(authorize, fixture.Contexts[0], 7001, Ct);
            await authorize.CommitAsync(Ct);
        }
        await using var replay = await fixture.Sessions.BeginAsync(Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => access.RequireCurrentAsync(replay, fixture.Contexts[0], 7001, Ct));
        Assert.Equal(DbSessionState.Aborted, replay.State);
    }

    [Fact]
    public async Task UnknownTransportAndForgedActorNeverProduceAccess()
    {
        await using var fixture = await BrandFixture.CreateAsync(Ct);
        await Channels(fixture);
        var policy = await Read(fixture, 0);
        Assert.Null(await BrandPolicy.CheckChannelsAsync(policy, 7001, (_, _, _) => throw new HttpRequestException("public synthetic private error"), Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => BrandPolicy.CheckChannelsAsync(policy, 7002, (_, _, _) => Task.FromResult(ChannelMembership.Member), Ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BrandPolicy.CheckChannelsAsync(policy, 7001, (_, _, _) => Task.FromResult(ChannelMembership.Member), new CancellationToken(true)));
    }

    [Fact]
    public async Task AuditedBrandBypassReplaysOnceAndPreservesChannelsAndOtherBrands()
    {
        await using var fixture = await BrandFixture.CreateAsync(Ct);
        await Channels(fixture);
        var admin = await Admin(fixture, false);
        using var token = admin.Ticket;
        ChannelAccess stale = (await BrandPolicy.CheckChannelsAsync(await Read(fixture, 0), 7001, (_, _, _) => Task.FromResult(ChannelMembership.Member), Ct))!;
        Guid command = Guid.NewGuid();
        for (int i = 0; i < 2; i++)
        {
            await using var update = await fixture.Sessions.BeginAsync(Ct);
            Assert.Equal(1, await BrandPolicyStore.SetEnforcementAsync(update, admin.Actor, fixture.Brands[0], 0, true, false, command, Guid.NewGuid(), Ct));
            await update.CommitAsync(Ct);
        }
        await using (var old = await fixture.Sessions.BeginAsync(Ct))
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => stale.RequireCurrentAsync(old, fixture.Contexts[0], 7001, Ct));
        BrandPolicySnapshot bypass = await Read(fixture, 0);
        Assert.False(bypass.EnforceChannels);
        Assert.NotNull(await BrandPolicy.CheckChannelsAsync(bypass, 7001, (_, _, _) => throw new InvalidOperationException("Bypassed channel transport was called."), Ct));
        Assert.True((await Read(fixture, 1)).EnforceChannels);
        await using (var verify = await fixture.Sessions.BeginAsync(Ct))
        {
            Assert.Equal(1L, await verify.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.audit_events WHERE id=$1 AND before_channel_enforcement AND NOT after_channel_enforcement", DbValue.Uuid(command)), row => row.GetInt64(0), Ct));
            Assert.Equal(9L, await verify.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.brand_channels"), row => row.GetInt64(0), Ct));
        }
        await using var restore = await fixture.Sessions.BeginAsync(Ct);
        Assert.Equal(2, await BrandPolicyStore.SetEnforcementAsync(restore, admin.Actor, fixture.Brands[0], 1, false, true, Guid.NewGuid(), Guid.NewGuid(), Ct));
        await restore.CommitAsync(Ct);
    }

    [Fact]
    public async Task VerifiedStaffBypassAllBrandsAndRevocationInvalidatesTheirProof()
    {
        await using var fixture = await BrandFixture.CreateAsync(Ct);
        await Channels(fixture);
        var admin = await Admin(fixture, true);
        using var token = admin.Ticket;
        var proofs = new List<ChannelAccess>();
        for (int i = 0; i < 3; i++)
        {
            BrandPolicySnapshot policy = await Read(fixture, i);
            Assert.True(policy.VerifiedStaff);
            proofs.Add((await BrandPolicy.CheckChannelsAsync(policy, 7001, (_, _, _) => throw new InvalidOperationException("Staff transport was called."), Ct))!);
        }
        await using (var revoke = await fixture.Sessions.BeginAsync(Ct))
        {
            await AdminRevocationStore.RevokeAsync(revoke, admin.Actor, admin.Actor.AdminId, admin.Actor.AuthVersion,
                Guid.NewGuid(), Guid.NewGuid(), revokeTelegram: true, cancellationToken: Ct);
            await revoke.CommitAsync(Ct);
        }
        for (int i = 0; i < 3; i++)
        {
            await using (var stale = await fixture.Sessions.BeginAsync(Ct))
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => proofs[i].RequireCurrentAsync(stale, fixture.Contexts[i], 7001, Ct));
            BrandPolicySnapshot ordinary = await Read(fixture, i);
            Assert.False(ordinary.VerifiedStaff);
            Assert.Null(await BrandPolicy.CheckChannelsAsync(ordinary, 7001, (_, _, _) => Task.FromResult(ChannelMembership.Left), Ct));
        }
    }

    [Fact]
    public async Task ContactAssetsAndLanguageRemainBrandScopedWithSafeFallback()
    {
        await using var fixture = await BrandFixture.CreateAsync(Ct);
        var admin = await Admin(fixture, true);
        using var token = admin.Ticket;
        await using (var configure = await fixture.Sessions.BeginAsync(Ct))
        {
            await configure.ExecuteAsync(new DbStatement("UPDATE shop.admins SET public_contact_handle='public_staff' WHERE id=$1", DbValue.Uuid(admin.Actor.AdminId)), Ct);
            await configure.ExecuteAsync(new DbStatement("UPDATE shop.brands SET customer_support_admin_id=$1,logo_asset_ref='public-logo-a' WHERE id=$2", DbValue.Uuid(admin.Actor.AdminId), DbValue.Uuid(fixture.Brands[0])), Ct);
            await BrandUserStore.SetPreferencesAsync(configure, fixture.Contexts[0], 7001, fixture.Users[0], "de", false, Ct);
            await configure.CommitAsync(Ct);
        }
        var a = await Read(fixture, 0);
        var b = await Read(fixture, 1);
        Assert.Equal("@public_staff", Localization.PublicContact(a));
        Assert.Null(Localization.PublicContact(b));
        Assert.Equal("public-logo-a", Localization.Asset(a));
        Assert.Null(Localization.Asset(b));
        Assert.Equal("ناموجود", Localization.Text(a, CustomerText.OutOfStock));
        Assert.Equal("Out of stock", Localization.Text(b, CustomerText.OutOfStock));
        Assert.DoesNotContain("public_staff", Localization.Text(b, CustomerText.RestockAvailable), StringComparison.Ordinal);
        Assert.Contains("does not reserve", Localization.Text(b, CustomerText.RestockAvailable), StringComparison.Ordinal);
        Assert.NotEqual(Localization.Text(a, CustomerText.RestockOptIn), Localization.Text(a, CustomerText.RestockUnsubscribe));
        await using (var disable = await fixture.Sessions.BeginAsync(Ct))
        {
            await AdminRevocationStore.RevokeAsync(disable, admin.Actor, admin.Actor.AdminId, admin.Actor.AuthVersion, Guid.NewGuid(), Guid.NewGuid(), disableAccount: true, cancellationToken: Ct);
            await disable.CommitAsync(Ct);
        }
        Assert.Null(Localization.PublicContact(await Read(fixture, 0)));
    }

    [Fact]
    public async Task ForeignProofAndChannelChangesFailCurrentPolicyValidation()
    {
        await using var fixture = await BrandFixture.CreateAsync(Ct);
        await Channels(fixture);
        ChannelAccess foreign = (await BrandPolicy.CheckChannelsAsync(await Read(fixture, 0), 7001, (_, _, _) => Task.FromResult(ChannelMembership.Member), Ct))!;
        await using (var wrong = await fixture.Sessions.BeginAsync(Ct))
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => foreign.RequireCurrentAsync(wrong, fixture.Contexts[1], 7001, Ct));
        ChannelAccess changed = (await BrandPolicy.CheckChannelsAsync(await Read(fixture, 0), 7001, (_, _, _) => Task.FromResult(ChannelMembership.Member), Ct))!;
        await using (var configure = await fixture.Sessions.BeginAsync(Ct))
        {
            await configure.ExecuteAsync(new DbStatement("UPDATE shop.brand_channels SET active=false WHERE brand_id=$1 AND chat_id=-1001", DbValue.Uuid(fixture.Brands[0])), Ct);
            await configure.CommitAsync(Ct);
        }
        await using var stale = await fixture.Sessions.BeginAsync(Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => changed.RequireCurrentAsync(stale, fixture.Contexts[0], 7001, Ct));
    }

    private static async Task Channels(BrandFixture fixture)
    {
        await using var seed = await fixture.Sessions.BeginAsync(Ct);
        foreach (Guid brand in fixture.Brands)
        {
            await seed.ExecuteAsync(new DbStatement("UPDATE shop.brands SET require_channel_join=true WHERE id=$1", DbValue.Uuid(brand)), Ct);
            await seed.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.brand_channels(brand_id,chat_id,title,required,active) VALUES ($1,-1001,'public required',true,true),
                    ($1,-1002,'public inactive',true,false),($1,-1003,'public optional',false,true)
                """, DbValue.Uuid(brand)), Ct);
        }
        await seed.CommitAsync(Ct);
    }

    private static async Task<BrandPolicySnapshot> Read(BrandFixture fixture, int index)
    {
        await using var read = await fixture.Sessions.BeginAsync(Ct);
        return await BrandPolicyStore.ReadAsync(read, fixture.Contexts[index], 7001, Ct);
    }

    private static async Task<(IssuedAdminSession Ticket, AuthenticatedAdmin Actor)> Admin(BrandFixture fixture, bool bind)
    {
        Guid id = Guid.NewGuid();
        await using (var seed = await fixture.Sessions.BeginAsync(Ct))
        {
            await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.admins(id,login,authentication_mode,password_verifier) VALUES ($1,'public-policy-admin','password',$2)", DbValue.Uuid(id), DbValue.Text(Verifier.Value)), Ct);
            await seed.CommitAsync(Ct);
        }
        var admin = await Login(fixture);
        if (!bind) return admin;
        using (admin.Ticket)
        {
            AdminToken challenge;
            await using (var begin = await fixture.Sessions.BeginAsync(Ct))
            {
                challenge = (await AdminBindingStore.BeginAsync(begin, admin.Actor, 7001, TimeSpan.FromMinutes(5), Guid.NewGuid(), Guid.NewGuid(), Ct))!;
                await begin.CommitAsync(Ct);
            }
            using (challenge)
            {
                await using (var proof = await fixture.Sessions.BeginAsync(Ct))
                {
                    Assert.True(await AdminBindingStore.ObservePrivateProofAsync(proof, id, challenge, PrivateTelegramProof.FromVerifiedIngress(fixture.Contexts[0], 7001, 7001, false, challenge), Ct));
                    await proof.CommitAsync(Ct);
                }
                await using var complete = await fixture.Sessions.BeginAsync(Ct);
                Assert.True(await AdminBindingStore.CompleteAsync(complete, admin.Actor, 7001, 0, challenge, Guid.NewGuid(), Guid.NewGuid(), Ct));
                await complete.CommitAsync(Ct);
            }
        }
        return await Login(fixture);
    }

    private static async Task<(IssuedAdminSession Ticket, AuthenticatedAdmin Actor)> Login(BrandFixture fixture)
    {
        IssuedAdminSession ticket;
        await using (var login = await fixture.Sessions.BeginAsync(Ct))
        {
            ticket = await AdminSessionStore.LoginAsync(login, "public-policy-admin", Password.AsMemory(), TimeSpan.FromHours(1), Ct);
            await login.CommitAsync(Ct);
        }
        await using var authenticate = await fixture.Sessions.BeginAsync(Ct);
        return (ticket, await AdminSessionStore.AuthenticateAsync(authenticate, ticket.Token, Ct));
    }
}
