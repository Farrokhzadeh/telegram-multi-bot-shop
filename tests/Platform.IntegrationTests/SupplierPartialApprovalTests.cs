using Platform.Fulfillment;
using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

[Collection("PostgreSQL races")]
public sealed class SupplierPartialApprovalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task NativeCompatiblePartialStatusExportRecoveryAndWholeAuditedRelease() => await SupplierPartialQualification.RunAsync(Ct);
    [Fact]
    public async Task PendingReviewAuditCannotDeadlockProtectedManifestValidation()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await SupplierFixture.ConfigureAsync(f, Ct);
        Guid order = await SupplierFixture.AcceptAsync(f, 5, Ct);
        var store = new SupplierRequestStore(f.Sessions);
        var port = new SyntheticSupplier { Submit = b => SyntheticSupplier.Complete(b, customers: 3) };
        JobLease lease = await SupplierFixture.ClaimAsync(f, JobKind.SubmitSupplier, Ct);
        Guid request = (await store.ReadAsync(f.Context(0), lease, Ct)).RequestId;
        await new SupplierFulfillmentWorker(store, port, port).RunAsync(f.Context(0), lease, Ct);
        await SupplierPartialQualification.ScheduleAsync(f, request, JobKind.ReconcileSupplier, Ct);
        await new SupplierFulfillmentWorker(store, port, port).RunAsync(f.Context(0), await SupplierFixture.ClaimAsync(f, JobKind.ReconcileSupplier, Ct), Ct);
        var actor = await f.AdminAsync(Ct);
        await using (var audit = await f.Sessions.BeginAsync(Ct))
        {
            Guid command = Guid.NewGuid();
            await AdminAuditStore.AppendAsync(audit, actor, new(command, AuditAction.Maintenance, AuditEntity.Order, order, f.Brands[0], AuditReason.OperatorReview, command, new byte[32]), Ct);
            var sessions = new DbSessionFactory(f.Database.Data, new() { LockTimeout = TimeSpan.FromMilliseconds(100) });
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new DeliveryStore(sessions).ReadManifestAsync(f.Context(0), CheckoutFixture.Actor(0), order, Ct));
        }
        await store.ApproveCompleteAsync(actor, f.Context(0), order, request, await SupplierPartialQualification.RevisionAsync(f, order, Ct), Guid.NewGuid(), "Public full acquisition review", Ct);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='prepare_manifest'", Ct));
    }
    [Theory]
    [InlineData("partial")]
    [InlineData("foreign")]
    [InlineData("stale")]
    [InlineData("cipher")]
    [InlineData("revoked")]
    [InlineData("commit")]
    public async Task ApprovalRejectsIncompleteForeignStaleUnreadableOrRevokedEvidenceWithoutClearingReview(string invalid)
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await SupplierFixture.ConfigureAsync(f, Ct);
        Guid order = await SupplierFixture.AcceptAsync(f, 5, Ct);
        var store = new SupplierRequestStore(f.Sessions);
        var port = new SyntheticSupplier { Submit = b => SyntheticSupplier.Complete(b, customers: 3) };
        JobLease lease = await SupplierFixture.ClaimAsync(f, JobKind.SubmitSupplier, Ct);
        Guid request = (await store.ReadAsync(f.Context(0), lease, Ct)).RequestId;
        await new SupplierFulfillmentWorker(store, port, port).RunAsync(f.Context(0), lease, Ct);
        if (invalid != "partial")
        {
            await SupplierPartialQualification.ScheduleAsync(f, request, JobKind.ReconcileSupplier, Ct);
            await new SupplierFulfillmentWorker(store, port, port).RunAsync(f.Context(0), await SupplierFixture.ClaimAsync(f, JobKind.ReconcileSupplier, Ct), Ct);
        }
        long revision = await SupplierPartialQualification.RevisionAsync(f, order, Ct);
        var actor = await f.AdminAsync(Ct);
        using var wrongFiles = invalid == "cipher" ? new SyntheticSecretFiles() : null;
        using var wrongKeys = wrongFiles is null ? null : SecretFiles.Load(wrongFiles.Root, "service-config.json", wrongFiles.Grant());
        var reviewer = wrongKeys is null ? store : new SupplierRequestStore(f.Sessions);
        if (invalid == "revoked")
        {
            await using var logout = await f.Sessions.BeginAsync(Ct);
            await AdminSessionStore.LogoutAsync(logout, actor, Ct); await logout.CommitAsync(Ct);
        }
        if (invalid == "commit")
        {
            await using var fault = f.Database.Data.CreateCommand("CREATE FUNCTION shop.reject_supplier_approval() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'Public approval fault' USING ERRCODE='23514'; END $$; CREATE CONSTRAINT TRIGGER reject_supplier_approval AFTER INSERT ON shop.jobs DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (NEW.job_kind='prepare_manifest') EXECUTE FUNCTION shop.reject_supplier_approval()");
            await fault.ExecuteNonQueryAsync(Ct);
        }
        var context = f.Context(invalid == "foreign" ? 1 : 0);
        var error = await Record.ExceptionAsync(() => reviewer.ApproveCompleteAsync(actor, context, order, request, invalid == "stale" ? revision + 1 : revision, Guid.NewGuid(), "Public reviewed acquisition", Ct));
        Assert.NotNull(error);
        if (invalid == "foreign") Assert.IsType<UnauthorizedAccessException>(error);
        else if (invalid == "revoked") Assert.IsType<AdminAuthenticationException>(error);
        else if (invalid == "cipher") Assert.IsType<CipherRejectedException>(error);
        else if (invalid == "commit") Assert.Equal(DbFailureKind.Constraint, Assert.IsType<DbOperationException>(error).Failure.Kind);
        else Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.audit_events WHERE action='manual_fulfillment'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE fulfillment_status='requires_admin' AND review_code='partial_acquisition' AND release_authorized_at IS NULL", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='prepare_manifest'", Ct));
    }
}
