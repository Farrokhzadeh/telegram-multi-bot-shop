using System.Security.Claims;
using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

[Collection("PostgreSQL races")]
public sealed class BrandWriterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static async Task<CheckoutResult> Purchase(CheckoutFixture fixture, int user, long revision = 0)
    {
        await using var session = await fixture.Sessions.BeginAsync(Ct);
        await BrandWriterStore.RequireAsync(session, fixture.Context(user), revision, Ct);
        var result = await fixture.Store.AcceptLocalAsync(session, fixture.Context(user), CheckoutFixture.Actor(user), fixture.Request() with { PolicyRevision = revision }, Ct);
        await session.CommitAsync(Ct);
        return result;
    }
    private static async Task Denied(CheckoutFixture fixture, BrandContext context, long revision)
    {
        await using var session = await fixture.Sessions.BeginAsync(Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => BrandWriterStore.RequireAsync(session, context, revision, Ct));
        Assert.Equal(DbSessionState.Aborted, session.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CommitAsync(Ct));
    }
    private static async Task WaitForBlock(CheckoutFixture fixture, int blocker)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(TimeSpan.FromSeconds(3));
        while (true)
        {
            await using var read = await fixture.Sessions.BeginAsync(limit.Token);
            bool blocked = await read.QuerySingleAsync(new DbStatement("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND $1=ANY(pg_blocking_pids(pid)))", DbValue.Integer(blocker)), r => r.GetBoolean(0), limit.Token);
            if (blocked) return;
            await Task.Delay(10, limit.Token);
        }
    }

    [Fact]
    public async Task PauseResumeIsAuditedAndOldCommandsNeverReverseNewState()
    {
        await using var fixture = await CheckoutFixture.CreateAsync(ct: Ct);
        var actor = await fixture.AdminAsync(Ct);
        var store = new BrandWriterStore(fixture.Sessions);
        var context = fixture.Context(0);
        Assert.Equal(new(true, 0), await store.ReadAsync(actor, context, Ct));
        Guid pause = Guid.NewGuid();
        Assert.Equal(new(false, 1), await store.SetActiveAsync(actor, context, 0, false, pause, Ct));
        await Denied(fixture, context, 0);
        await Denied(fixture, context, 1);
        Assert.Equal(new(false, 1), await store.SetActiveAsync(actor, context, 0, false, pause, Ct));
        Assert.Equal(new(true, 2), await store.SetActiveAsync(actor, context, 1, true, Guid.NewGuid(), Ct));
        Assert.Equal(new(false, 1), await store.SetActiveAsync(actor, context, 0, false, pause, Ct));
        Assert.Equal(new(true, 2), await store.ReadAsync(actor, context, Ct));
        var conflict = await Assert.ThrowsAsync<DbOperationException>(() => store.SetActiveAsync(actor, context, 0, true, pause, Ct));
        Assert.Equal(DbFailureKind.InvalidData, conflict.Failure.Kind);
        Assert.Equal(DbRecovery.Reject, conflict.Failure.Recovery);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.SetActiveAsync(actor, context, 0, false, Guid.NewGuid(), Ct));
        await Denied(fixture, context, 0);
        Assert.Equal(CheckoutOutcome.Purchased, (await Purchase(fixture, 0, 2)).Outcome);
        Assert.Equal(2L, await fixture.CountAsync("SELECT count(*) FROM shop.audit_events WHERE entity_kind='brand' AND action IN ('disable','enable')", Ct));
        Assert.Equal(2L, await fixture.CountAsync("SELECT count(*) FROM shop.brands WHERE active AND policy_version=0", Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PauseDrainsAdmittedWorkWhileOtherBrandsBuyAndClaimTheirOwnJobs(bool commit)
    {
        await using var fixture = await CheckoutFixture.CreateAsync(stock: 3, ct: Ct);
        Assert.Equal(CheckoutOutcome.Purchased, (await Purchase(fixture, 0)).Outcome);
        var actor = await fixture.AdminAsync(Ct);
        var store = new BrandWriterStore(fixture.Sessions);
        await using var admitted = await fixture.Sessions.BeginAsync(Ct);
        int pid = await admitted.QuerySingleAsync(new DbStatement("SELECT pg_backend_pid()"), r => r.GetInt32(0), Ct);
        JobLease lease = Assert.IsType<JobLease>(await OutboxStore.ClaimForBrandAsync(admitted, fixture.Context(0), 0, JobKind.PrepareManifest, Guid.NewGuid(), TimeSpan.FromMinutes(5), Ct));
        Task<BrandWriterState> pause = store.SetActiveAsync(actor, fixture.Context(0), 0, false, Guid.NewGuid(), Ct);
        try
        {
            await WaitForBlock(fixture, pid);
            Assert.False(pause.IsCompleted);
            Assert.Equal(CheckoutOutcome.Purchased, (await Purchase(fixture, 1)).Outcome);
            Assert.Equal(CheckoutOutcome.Purchased, (await Purchase(fixture, 2)).Outcome);
        }
        finally { if (commit) await admitted.CommitAsync(Ct); else await admitted.RollbackAsync(Ct); }
        Assert.Equal(new(false, 1), await pause);
        await Denied(fixture, fixture.Context(0), 1);
        await using (var second = await fixture.Sessions.BeginAsync(Ct))
        {
            JobLease other = Assert.IsType<JobLease>(await OutboxStore.ClaimForBrandAsync(second, fixture.Context(1), 0, JobKind.PrepareManifest, Guid.NewGuid(), TimeSpan.FromMinutes(5), Ct));
            Assert.Equal(fixture.Brands[1], other.Intent.BrandId);
            await OutboxStore.CompleteAsync(second, other, Ct);
            await second.CommitAsync(Ct);
        }
        Assert.Equal(new(true, 2), await store.SetActiveAsync(actor, fixture.Context(0), 1, true, Guid.NewGuid(), Ct));
        await using (var finish = await fixture.Sessions.BeginAsync(Ct))
        {
            await BrandWriterStore.RequireAsync(finish, fixture.Context(0), 2, Ct);
            if (!commit) lease = Assert.IsType<JobLease>(await OutboxStore.ClaimForBrandAsync(finish, fixture.Context(0), 2, JobKind.PrepareManifest, Guid.NewGuid(), TimeSpan.FromMinutes(5), Ct));
            Assert.Equal(fixture.Brands[0], lease.Intent.BrandId);
            Assert.Equal(1L, lease.Attempt);
            await OutboxStore.CompleteAsync(finish, lease, Ct);
            await finish.CommitAsync(Ct);
        }
        Assert.Equal(3L, await fixture.CountAsync("SELECT count(*) FROM shop.orders", Ct));
        Assert.Equal(3L, await fixture.CountAsync("SELECT count(*) FROM shop.wallets WHERE balance=90 AND version=2", Ct));
        Assert.Equal(3L, await fixture.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", Ct));
        Assert.Equal(3L, await fixture.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='allocated'", Ct));
        Assert.Equal(2L, await fixture.CountAsync("SELECT count(*) FROM shop.jobs WHERE status='succeeded'", Ct));
        Assert.Equal(1L, await fixture.CountAsync("SELECT count(*) FROM shop.jobs WHERE status='pending' AND attempts=0", Ct));
    }

    [Fact]
    public async Task LostAdmissionConnectionRollsBackLeaseAndAllowsPauseWithoutLosingItsOrder()
    {
        await using var fixture = await CheckoutFixture.CreateAsync(stock: 2, ct: Ct);
        Assert.Equal(CheckoutOutcome.Purchased, (await Purchase(fixture, 0)).Outcome);
        var actor = await fixture.AdminAsync(Ct);
        await using var owner = await fixture.Sessions.BeginAsync(Ct);
        int pid = await owner.QuerySingleAsync(new DbStatement("SELECT pg_backend_pid()"), r => r.GetInt32(0), Ct);
        Assert.NotNull(await OutboxStore.ClaimForBrandAsync(owner, fixture.Context(0), 0, JobKind.PrepareManifest, Guid.NewGuid(), TimeSpan.FromMinutes(5), Ct));
        var store = new BrandWriterStore(fixture.Sessions);
        Task<BrandWriterState> pause = store.SetActiveAsync(actor, fixture.Context(0), 0, false, Guid.NewGuid(), Ct);
        await WaitForBlock(fixture, pid);
        await using (var control = await fixture.Sessions.BeginAsync(Ct))
            Assert.True(await control.QuerySingleAsync(new DbStatement("SELECT pg_terminate_backend($1,5000)", DbValue.Integer(pid)), r => r.GetBoolean(0), Ct));
        Assert.Equal(new(false, 1), await pause);
        await Denied(fixture, fixture.Context(0), 0);
        Assert.Equal(CheckoutOutcome.Purchased, (await Purchase(fixture, 1)).Outcome);
        Assert.Equal(2L, await fixture.CountAsync("SELECT count(*) FROM shop.orders", Ct));
        Assert.Equal(2L, await fixture.CountAsync("SELECT count(*) FROM shop.jobs WHERE status='pending' AND attempts=0", Ct));
    }

    [Fact]
    public async Task RevokedAdministratorCannotPauseOrResume()
    {
        await using var fixture = await CheckoutFixture.CreateAsync(ct: Ct);
        var actor = await fixture.AdminAsync(Ct);
        await using (var logout = await fixture.Sessions.BeginAsync(Ct))
        { await AdminSessionStore.LogoutAsync(logout, actor, Ct); await logout.CommitAsync(Ct); }
        var store = new BrandWriterStore(fixture.Sessions);
        await Assert.ThrowsAsync<AdminAuthenticationException>(() => store.SetActiveAsync(actor, fixture.Context(0), 0, false, Guid.NewGuid(), Ct));
        Assert.Equal(3L, await fixture.CountAsync("SELECT count(*) FROM shop.brands WHERE active AND policy_version=0", Ct));
        Assert.Equal(0L, await fixture.CountAsync("SELECT count(*) FROM shop.audit_events WHERE entity_kind='brand'", Ct));
    }

    [Fact]
    public async Task FailedRevisionOrForeignBotAdmissionCannotCommitCheckoutOrWork()
    {
        await using var fixture = await CheckoutFixture.CreateAsync(ct: Ct);
        await using (var session = await fixture.Sessions.BeginAsync(Ct))
        {
            Assert.Equal(CheckoutOutcome.Purchased, (await fixture.Store.AcceptLocalAsync(session, fixture.Context(0), CheckoutFixture.Actor(0), fixture.Request(), Ct)).Outcome);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => BrandWriterStore.RequireAsync(session, fixture.Context(0), 1, Ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.CommitAsync(Ct));
        }
        var registry = new BotBrandRegistry([new("writer-test", "foreign-bot", fixture.Brands[0], 9999, SyntheticSecretFiles.Deployment, 7, true)]);
        Assert.True(registry.TryResolveAuthenticated("writer-test", new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "foreign-bot")], "writer-test")), out var foreign));
        await Denied(fixture, foreign!, 0);
        Assert.Equal(0L, await fixture.CountAsync("SELECT count(*) FROM shop.orders", Ct));
        Assert.Equal(0L, await fixture.CountAsync("SELECT count(*) FROM shop.jobs", Ct));
        Assert.Equal(0L, await fixture.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", Ct));
        Assert.Equal(1L, await fixture.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='available'", Ct));
    }
}
