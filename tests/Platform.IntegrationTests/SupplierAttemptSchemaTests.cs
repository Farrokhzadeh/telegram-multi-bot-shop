using Platform.Core;
using Platform.Fulfillment;
using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class SupplierAttemptSchemaTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task ExportRequiresItsEarlierSameRequestSubmitAndKeepsTerminalEvidenceImmutable()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await SupplierFixture.ConfigureAsync(f, Ct);
        Guid order = await SupplierFixture.AcceptAsync(f, 5, Ct), otherOrder = await SupplierFixture.AcceptAsync(f, 1, Ct);
        Guid request, otherRequest, submit = Guid.NewGuid(), export = Guid.NewGuid();
        await using (var read = await f.Sessions.BeginAsync(Ct))
        {
            request = await read.QuerySingleAsync(new DbStatement("SELECT id FROM shop.supplier_requests WHERE order_id=$1", DbValue.Uuid(order)), r => r.GetGuid(0), Ct);
            otherRequest = await read.QuerySingleAsync(new DbStatement("SELECT id FROM shop.supplier_requests WHERE order_id=$1", DbValue.Uuid(otherOrder)), r => r.GetGuid(0), Ct);
        }
        await using (var seed = await f.Sessions.BeginAsync(Ct))
        {
            await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.supplier_request_attempts(id,supplier_request_id,attempt_no,operation,max_charge_amount) VALUES($1,$2,1,'submit',1.1)", DbValue.Uuid(submit), DbValue.Uuid(request)), Ct);
            await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.supplier_request_attempts(id,supplier_request_id,attempt_no,operation,parent_submit_attempt_id) VALUES($1,$2,2,'export',$3)", DbValue.Uuid(export), DbValue.Uuid(request), DbValue.Uuid(submit)), Ct);
            await seed.CommitAsync(Ct);
        }
        await using (var foreign = await f.Sessions.BeginAsync(Ct))
        {
            var error = await Assert.ThrowsAsync<DbOperationException>(() => foreign.ExecuteAsync(new DbStatement("INSERT INTO shop.supplier_request_attempts(id,supplier_request_id,attempt_no,operation,parent_submit_attempt_id) VALUES(gen_random_uuid(),$1,1,'export',$2)", DbValue.Uuid(otherRequest), DbValue.Uuid(submit)), Ct));
            Assert.Equal(DbFailureKind.Constraint, error.Failure.Kind);
        }
        await using (var finish = await f.Sessions.BeginAsync(Ct))
        {
            await finish.ExecuteAsync(new DbStatement("UPDATE shop.supplier_request_attempts SET outcome='unknown',finished_at=CURRENT_TIMESTAMP,evidence_ref='protected:synthetic/export' WHERE id=$1", DbValue.Uuid(export)), Ct);
            await finish.CommitAsync(Ct);
        }
        await using (var changed = await f.Sessions.BeginAsync(Ct))
        {
            var error = await Assert.ThrowsAsync<DbOperationException>(() => changed.ExecuteAsync(new DbStatement("UPDATE shop.supplier_request_attempts SET evidence_ref='protected:synthetic/changed' WHERE id=$1", DbValue.Uuid(export)), Ct));
            Assert.Equal(DbFailureKind.Constraint, error.Failure.Kind);
        }
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.supplier_request_attempts WHERE operation='export' AND outcome='unknown' AND parent_submit_attempt_id IS NOT NULL AND purchase_idempotency_key IS NULL AND max_charge_amount IS NULL AND admin_authorization_audit_id IS NULL", Ct));
    }
    [Fact]
    public async Task AttemptTableHasTypedColumnsUniqueKeyImmutableHistoryAndTriggerGuard()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await SupplierFixture.ConfigureAsync(f, Ct);
        await using var read = await f.Sessions.BeginAsync(Ct);
        var columns = await read.QueryManyAsync(new DbStatement("SELECT column_name||':'||coalesce(domain_name,data_type)||':'||is_nullable FROM information_schema.columns WHERE table_schema='shop' AND table_name='supplier_request_attempts' ORDER BY ordinal_position"), r => r.GetString(0), 16, Ct);
        Assert.Equal(new[] { "id:uuid:NO", "supplier_request_id:uuid:NO", "attempt_no:bigint:NO", "operation:text:NO", "outcome:text:NO", "parent_submit_attempt_id:uuid:YES", "admin_authorization_audit_id:uuid:YES", "purchase_idempotency_key:short_text:YES", "max_charge_amount:finite_numeric:YES", "http_status:integer:YES", "error_code:short_text:YES", "remote_ref:short_text:YES", "evidence_ref:asset_ref:YES", "started_at:timestamp with time zone:NO", "finished_at:timestamp with time zone:YES" }, columns);
        var requestColumns = await read.QueryManyAsync(new DbStatement("SELECT column_name::text FROM information_schema.columns WHERE table_schema='shop' AND table_name='supplier_requests' ORDER BY ordinal_position"), r => r.GetString(0), 26, Ct);
        Assert.Equal(new[] { "id", "order_id", "requested_quantity", "product_supplier_source_id", "source_revision", "provider_code", "account_ref", "external_product_id", "external_variant_id", "local_intent_key", "remote_ref", "request_sha256", "status", "revision", "review_code", "next_check_at", "evidence_ref", "cost_baseline_amount", "cost_currency", "cost_quote_ref", "cost_observed_at", "approved_cost_ceiling", "approval_audit_id", "created_at", "updated_at" }, requestColumns);
        long uniqueKey = await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM pg_indexes WHERE schemaname='shop' AND tablename='supplier_request_attempts' AND indexname='supplier_request_attempts_supplier_request_id_attempt_no_key'"), r => r.GetInt64(0), Ct);
        Assert.Equal(1L, uniqueKey);
        long deleteGuard = await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM pg_trigger WHERE tgrelid='shop.supplier_request_attempts'::regclass AND tgname='supplier_request_attempts_no_delete'"), r => r.GetInt64(0), Ct);
        Assert.Equal(1L, deleteGuard);
        long truncateGuard = await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM pg_trigger WHERE tgrelid='shop.supplier_request_attempts'::regclass AND tgname='supplier_request_attempts_no_truncate'"), r => r.GetInt64(0), Ct);
        Assert.Equal(1L, truncateGuard);
        long attemptGuard = await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM pg_trigger WHERE tgrelid='shop.supplier_request_attempts'::regclass AND tgname='attempts_guard'"), r => r.GetInt64(0), Ct);
        Assert.Equal(1L, attemptGuard);
        long retryIndex = await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM pg_indexes WHERE schemaname='shop' AND tablename='supplier_request_attempts' AND indexname='attempt_retry_authorization_once'"), r => r.GetInt64(0), Ct);
        Assert.Equal(1L, retryIndex);
    }
    [Fact]
    public async Task SubmitAttemptRequiresSequentialNoAndInFlightInitialOutcomeWithCostCeiling()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await SupplierFixture.ConfigureAsync(f, Ct);
        await SupplierFixture.AcceptAsync(f, 5, Ct);
        Guid request;
        await using (var r = await f.Sessions.BeginAsync(Ct))
        { request = await r.QuerySingleAsync(new DbStatement("SELECT id FROM shop.supplier_requests"), r => r.GetGuid(0), Ct); }
        await using (var invalid = await f.Sessions.BeginAsync(Ct))
        {
            var err = await Assert.ThrowsAsync<DbOperationException>(() => invalid.ExecuteAsync(new DbStatement("INSERT INTO shop.supplier_request_attempts(id,supplier_request_id,attempt_no,operation,max_charge_amount,outcome) VALUES (gen_random_uuid(),$1,3,'submit',1.1,'in_flight')", DbValue.Uuid(request)), Ct));
            Assert.Equal(DbFailureKind.Constraint, err.Failure.Kind);
        }
        await using (var invalid = await f.Sessions.BeginAsync(Ct))
        {
            var err = await Assert.ThrowsAsync<DbOperationException>(() => invalid.ExecuteAsync(new DbStatement("INSERT INTO shop.supplier_request_attempts(id,supplier_request_id,attempt_no,operation,max_charge_amount,outcome) VALUES (gen_random_uuid(),$1,1,'submit',1.1,'succeeded')", DbValue.Uuid(request)), Ct));
            Assert.Equal(DbFailureKind.Constraint, err.Failure.Kind);
        }
        await using (var valid = await f.Sessions.BeginAsync(Ct))
        {
            await valid.ExecuteAsync(new DbStatement("INSERT INTO shop.supplier_request_attempts(id,supplier_request_id,attempt_no,operation,max_charge_amount) VALUES (gen_random_uuid(),$1,1,'submit',1.1)", DbValue.Uuid(request)), Ct);
            await valid.CommitAsync(Ct);
        }
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.supplier_request_attempts WHERE outcome='in_flight' AND attempt_no=1 AND operation='submit'", Ct));
    }
    [Fact]
    public async Task TerminalOutcomeIsWrittenOnceAndOriginalIntentFieldsCannotChange()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await SupplierFixture.ConfigureAsync(f, Ct);
        Guid order = await SupplierFixture.AcceptAsync(f, 5, Ct);
        var store = new SupplierRequestStore(f.Sessions, f.Secrets);
        var port = new SyntheticSupplier { Submit = _ => SyntheticSupplier.Unknown() };
        var lease = await SupplierFixture.ClaimAsync(f, JobKind.SubmitSupplier, Ct);
        await new SupplierFulfillmentWorker(store, port, port).RunAsync(f.Context(0), lease, Ct);
        Guid attempt;
        await using (var r = await f.Sessions.BeginAsync(Ct))
        { attempt = await r.QuerySingleAsync(new DbStatement("SELECT id FROM shop.supplier_request_attempts"), r => r.GetGuid(0), Ct); }
        await using (var invalid = await f.Sessions.BeginAsync(Ct))
        {
            var err = await Assert.ThrowsAsync<DbOperationException>(() => invalid.ExecuteAsync(new DbStatement("UPDATE shop.supplier_request_attempts SET outcome='succeeded' WHERE id=$1 AND outcome='unknown'", DbValue.Uuid(attempt)), Ct));
            Assert.Equal(DbFailureKind.Constraint, err.Failure.Kind);
        }
        await using (var invalid = await f.Sessions.BeginAsync(Ct))
        {
            var err = await Assert.ThrowsAsync<DbOperationException>(() => invalid.ExecuteAsync(new DbStatement("UPDATE shop.supplier_request_attempts SET operation='export' WHERE id=$1", DbValue.Uuid(attempt)), Ct));
            Assert.Equal(DbFailureKind.Constraint, err.Failure.Kind);
        }
        await using (var invalid = await f.Sessions.BeginAsync(Ct))
        {
            var err = await Assert.ThrowsAsync<DbOperationException>(() => invalid.ExecuteAsync(new DbStatement("DELETE FROM shop.supplier_request_attempts WHERE id=$1", DbValue.Uuid(attempt)), Ct));
            Assert.Equal(DbFailureKind.Constraint, err.Failure.Kind);
        }
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.supplier_request_attempts", Ct));
    }
    [Fact]
    public async Task LookupAttemptRequiresParentSubmitAndCannotCarryCostCeilingOrRetryAudit()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await SupplierFixture.ConfigureAsync(f, Ct);
        await SupplierFixture.AcceptAsync(f, 5, Ct);
        var store = new SupplierRequestStore(f.Sessions, f.Secrets);
        var port = new SyntheticSupplier { Submit = _ => SyntheticSupplier.Unknown() };
        await new SupplierFulfillmentWorker(store, port, port).RunAsync(f.Context(0), await SupplierFixture.ClaimAsync(f, JobKind.SubmitSupplier, Ct), Ct);
        Guid request, submit;
        await using (var r = await f.Sessions.BeginAsync(Ct))
        { (request, submit) = await r.QuerySingleAsync(new DbStatement("SELECT r.id,a.id FROM shop.supplier_requests r JOIN shop.supplier_request_attempts a ON a.supplier_request_id=r.id"), r => (r.GetGuid(0), r.GetGuid(1)), Ct); }
        await using (var invalid = await f.Sessions.BeginAsync(Ct))
        {
            var err = await Assert.ThrowsAsync<DbOperationException>(() => invalid.ExecuteAsync(new DbStatement("INSERT INTO shop.supplier_request_attempts(id,supplier_request_id,attempt_no,operation,parent_submit_attempt_id) VALUES (gen_random_uuid(),$1,2,'status_lookup',NULL)", DbValue.Uuid(request)), Ct));
            Assert.Equal(DbFailureKind.Constraint, err.Failure.Kind);
        }
        await using (var invalid = await f.Sessions.BeginAsync(Ct))
        {
            var err = await Assert.ThrowsAsync<DbOperationException>(() => invalid.ExecuteAsync(new DbStatement("INSERT INTO shop.supplier_request_attempts(id,supplier_request_id,attempt_no,operation,parent_submit_attempt_id,max_charge_amount) VALUES (gen_random_uuid(),$1,2,'status_lookup',$2,1.1)", DbValue.Uuid(request), DbValue.Uuid(submit)), Ct));
            Assert.Equal(DbFailureKind.Constraint, err.Failure.Kind);
        }
        await using (var valid = await f.Sessions.BeginAsync(Ct))
        {
            await valid.ExecuteAsync(new DbStatement("INSERT INTO shop.supplier_request_attempts(id,supplier_request_id,attempt_no,operation,parent_submit_attempt_id) VALUES (gen_random_uuid(),$1,2,'status_lookup',$2)", DbValue.Uuid(request), DbValue.Uuid(submit)), Ct);
            await valid.CommitAsync(Ct);
        }
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.supplier_request_attempts WHERE operation='status_lookup' AND max_charge_amount IS NULL AND admin_authorization_audit_id IS NULL", Ct));
    }
    [Fact]
    public async Task SecondSubmitRequiresExplicitRetryAuthorizationAndUnconfirmedPriorSubmitBlocksIt()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await SupplierFixture.ConfigureAsync(f, Ct);
        await SupplierFixture.AcceptAsync(f, 5, Ct);
        var port = new SyntheticSupplier { Submit = _ => SyntheticSupplier.Unknown() };
        await new SupplierFulfillmentWorker(new(f.Sessions, f.Secrets), port, port).RunAsync(f.Context(0), await SupplierFixture.ClaimAsync(f, JobKind.SubmitSupplier, Ct), Ct);
        Guid request;
        await using (var r = await f.Sessions.BeginAsync(Ct)) request = await r.QuerySingleAsync(new DbStatement("SELECT id FROM shop.supplier_requests"), r => r.GetGuid(0), Ct);
        await using (var invalid = await f.Sessions.BeginAsync(Ct))
        {
            var err = await Assert.ThrowsAsync<DbOperationException>(() => invalid.ExecuteAsync(new DbStatement("INSERT INTO shop.supplier_request_attempts(id,supplier_request_id,attempt_no,operation,max_charge_amount) VALUES (gen_random_uuid(),$1,2,'submit',1.1)", DbValue.Uuid(request)), Ct));
            Assert.Equal(DbFailureKind.Constraint, err.Failure.Kind);
        }
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.supplier_request_attempts WHERE operation='submit'", Ct));
    }
    [Fact]
    public async Task UniqueAttemptNoRejectsGapSkipAndDuplicateNumberInSameRequest()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await SupplierFixture.ConfigureAsync(f, Ct);
        await SupplierFixture.AcceptAsync(f, 5, Ct);
        var port = new SyntheticSupplier { Submit = _ => SyntheticSupplier.Unknown() };
        await new SupplierFulfillmentWorker(new(f.Sessions, f.Secrets), port, port).RunAsync(f.Context(0), await SupplierFixture.ClaimAsync(f, JobKind.SubmitSupplier, Ct), Ct);
        Guid request, submit;
        await using (var r = await f.Sessions.BeginAsync(Ct))
        { (request, submit) = await r.QuerySingleAsync(new DbStatement("SELECT r.id,a.id FROM shop.supplier_requests r JOIN shop.supplier_request_attempts a ON a.supplier_request_id=r.id"), r => (r.GetGuid(0), r.GetGuid(1)), Ct); }
        await using (var valid = await f.Sessions.BeginAsync(Ct))
        { await valid.ExecuteAsync(new DbStatement("INSERT INTO shop.supplier_request_attempts(id,supplier_request_id,attempt_no,operation,parent_submit_attempt_id) VALUES (gen_random_uuid(),$1,2,'status_lookup',$2)", DbValue.Uuid(request), DbValue.Uuid(submit)), Ct); await valid.CommitAsync(Ct); }
        await using (var invalid = await f.Sessions.BeginAsync(Ct))
        {
            var err = await Assert.ThrowsAsync<DbOperationException>(() => invalid.ExecuteAsync(new DbStatement("INSERT INTO shop.supplier_request_attempts(id,supplier_request_id,attempt_no,operation,parent_submit_attempt_id) VALUES (gen_random_uuid(),$1,2,'status_lookup',$2)", DbValue.Uuid(request), DbValue.Uuid(submit)), Ct));
            Assert.Equal(DbFailureKind.Constraint, err.Failure.Kind);
        }
        await using (var invalid = await f.Sessions.BeginAsync(Ct))
        {
            var err = await Assert.ThrowsAsync<DbOperationException>(() => invalid.ExecuteAsync(new DbStatement("INSERT INTO shop.supplier_request_attempts(id,supplier_request_id,attempt_no,operation,parent_submit_attempt_id) VALUES (gen_random_uuid(),$1,4,'status_lookup',$2)", DbValue.Uuid(request), DbValue.Uuid(submit)), Ct));
            Assert.Equal(DbFailureKind.Constraint, err.Failure.Kind);
        }
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.supplier_request_attempts", Ct));
    }
    [Fact]
    public async Task WorkerProducesTypedAttemptEvidenceForSubmitAndLookupAndNeverRepurchases()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await SupplierFixture.ConfigureAsync(f, Ct);
        await SupplierFixture.AcceptAsync(f, 5, Ct);
        var port = new SyntheticSupplier { Submit = _ => SyntheticSupplier.Unknown() };
        var store = new SupplierRequestStore(f.Sessions, f.Secrets);
        await new SupplierFulfillmentWorker(store, port, port).RunAsync(f.Context(0), await SupplierFixture.ClaimAsync(f, JobKind.SubmitSupplier, Ct), Ct);
        Guid request;
        await using (var r = await f.Sessions.BeginAsync(Ct)) request = await r.QuerySingleAsync(new DbStatement("SELECT id FROM shop.supplier_requests"), r => r.GetGuid(0), Ct);
        await SupplierPartialQualification.ScheduleAsync(f, request, JobKind.ReconcileSupplier, Ct);
        port.Lookup = _ => SyntheticSupplier.Complete(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, "synthetic", "public account", "public product", "public variant", "public intent", 5, 1, "USD", null));
        await new SupplierFulfillmentWorker(new(f.Sessions, f.Secrets), port, port).RunAsync(f.Context(0), await SupplierFixture.ClaimAsync(f, JobKind.ReconcileSupplier, Ct), Ct);
        Assert.Equal(1, port.Submits);
        Assert.Equal(1, port.Lookups);
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.supplier_request_attempts WHERE finished_at IS NOT NULL", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.supplier_request_attempts WHERE operation='submit' AND outcome='unknown' AND evidence_ref IS NOT NULL", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.supplier_request_attempts WHERE operation='status_lookup' AND outcome='succeeded' AND parent_submit_attempt_id IS NOT NULL AND evidence_ref IS NOT NULL", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.supplier_request_attempts a JOIN shop.supplier_request_attempts s ON s.id=a.parent_submit_attempt_id WHERE a.operation='status_lookup'", Ct));
    }
    [Fact]
    public async Task AttemptSchemaRejectsJsonbAndRequiresFiniteTimestampsAndKnownOperations()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await using var read = await f.Sessions.BeginAsync(Ct);
        Assert.Equal(0L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM information_schema.columns WHERE table_schema='shop' AND table_name='supplier_request_attempts' AND data_type IN ('json','jsonb','ARRAY')"), r => r.GetInt64(0), Ct));
        Assert.Equal(1L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM pg_constraint WHERE conrelid='shop.supplier_request_attempts'::regclass AND conname='supplier_request_attempts_outcome_check'"), r => r.GetInt64(0), Ct));
        Assert.Equal(1L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM pg_constraint WHERE conrelid='shop.supplier_request_attempts'::regclass AND conname='supplier_request_attempts_operation_check'"), r => r.GetInt64(0), Ct));
    }
}
