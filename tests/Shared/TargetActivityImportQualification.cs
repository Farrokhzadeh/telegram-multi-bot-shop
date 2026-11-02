using System.Security.Cryptography;
using Platform.Core;
using Platform.Migration;
using Platform.Persistence;

namespace Platform.Testing;

public static class TargetActivityImportQualification
{
    public static async Task RunAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: ct); await EnableAsync(f, ct);
        DateTime now = await Clock(f, ct);
        await using (var seed = await f.Sessions.BeginAsync(ct))
        { await TelegramReplayStore.TryClaimAsync(seed, f.Context(0), 1000, ct); await seed.CommitAsync(ct); }
        TargetActivityRow[] rows = [new(Guid.NewGuid(), f.Users[0], BotActivityAction.CatalogOpen, BotActivitySource.Command, BotActivityOutcome.Accepted, new(TelegramUpdate: 1000), now.AddHours(-1), TargetActivityOrigin.Telegram, Guid.Empty),
            new(Guid.NewGuid(), f.Users[0], BotActivityAction.QuantityEnter, BotActivitySource.Flow, BotActivityOutcome.Accepted, new(f.Product, Quantity: 2), now.AddDays(-181), TargetActivityOrigin.Workflow, Guid.NewGuid()),
            new(Guid.NewGuid(), f.Users[0], BotActivityAction.DiscountsOpen, BotActivitySource.Flow, BotActivityOutcome.Rejected, new(), now.AddDays(-179), TargetActivityOrigin.CouponLookup, Guid.NewGuid())];
        var batch = Batch(f, now, rows); string before = await WorkflowActivityQualification.DomainAsync(f.Sessions, true, ct);
        var result = await new TargetActivityImportWorkflow(f.Sessions).ImportAsync(batch, ct);
        Require(result == new TargetActivityImportResult(2, 0, 1), "original expired observations are discarded before insertion");
        for (int i = 0; i < 100; i++) Require(await new TargetActivityImportWorkflow(f.Sessions).ImportAsync(batch, ct) == new TargetActivityImportResult(0, 2, 1), "restart and replay preserve original identities and clocks");
        await using (var read = await f.Sessions.BeginAsync(ct))
            Require(await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.bot_activity_events WHERE id=$1 AND occurred_at=$2 AND telegram_update_id=1000", DbValue.Uuid(rows[0].Id), DbValue.Utc(rows[0].OccurredAt)), r => r.GetInt64(0), ct) == 1, "original source ID and UTC occurrence survive import");
        var changed = rows.ToArray(); changed[0] = rows[0] with { Action = BotActivityAction.Start };
        bool conflict = false;
        try { await new TargetActivityImportWorkflow(f.Sessions).ImportAsync(Batch(f, now, changed), ct); } catch (MigrationRejectedException) { conflict = true; }
        Require(conflict && before == await WorkflowActivityQualification.DomainAsync(f.Sessions, true, ct) && await f.CountAsync("SELECT count(*) FROM shop.bot_activity_events", ct) == 2, "changed replay conflicts without domain mutation or duplicate history");
        byte[] body = VerifiedTargetActivityBatch.Encode(batch.Destination, batch.Brand, now, rows), digest = SHA256.HashData(body); digest[0] ^= 1;
        bool tampered = false;
        try { VerifiedTargetActivityBatch.Open(batch.Destination, batch.Brand, now, rows, digest); } catch (MigrationRejectedException) { tampered = true; }
        Require(tampered, "checksum is bound to exact rows and destination");
    }
    public static async Task AtomicAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: ct); await EnableAsync(f, ct); DateTime now = await Clock(f, ct);
        await using (var seed = await f.Sessions.BeginAsync(ct))
        { await TelegramReplayStore.TryClaimAsync(seed, f.Context(0), 1000, ct); await TelegramReplayStore.TryClaimAsync(seed, f.Context(0), 1001, ct); await seed.CommitAsync(ct); }
        TargetActivityRow[] rows = [new(Guid.NewGuid(), f.Users[0], BotActivityAction.Start, BotActivitySource.Command, BotActivityOutcome.Accepted, new(TelegramUpdate: 1000), now.AddMinutes(-1), TargetActivityOrigin.Telegram, Guid.Empty),
            new(Guid.NewGuid(), f.Users[0], BotActivityAction.CheckoutOpen, BotActivitySource.Callback, BotActivityOutcome.Accepted, new(f.Product, Guid.NewGuid(), TelegramUpdate: 1001), now.AddMinutes(-1), TargetActivityOrigin.Telegram, Guid.Empty)];
        bool rejected = false;
        try { await new TargetActivityImportWorkflow(f.Sessions).ImportAsync(Batch(f, now, rows), ct); } catch (MigrationRejectedException) { rejected = true; }
        Require(rejected && await f.CountAsync("SELECT count(*) FROM shop.bot_activity_events", ct) == 0, "one invalid context rolls back the whole bounded batch");
        rows[1] = rows[1] with { Action = BotActivityAction.Start, Source = BotActivitySource.Command, Context = new(TelegramUpdate: 1001) };
        var batch = Batch(f, now, rows);
        await using (var fail = f.Database.Data.CreateCommand("CREATE FUNCTION shop.synthetic_import_failure() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION USING ERRCODE='23514',MESSAGE='synthetic import failure'; END $$; CREATE CONSTRAINT TRIGGER synthetic_import_failure AFTER INSERT ON shop.bot_activity_events DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION shop.synthetic_import_failure()")) await fail.ExecuteNonQueryAsync(ct);
        bool failed = false;
        try { await new TargetActivityImportWorkflow(f.Sessions).ImportAsync(batch, ct); } catch (DbOperationException e) when (e.Failure.Kind == DbFailureKind.Constraint) { failed = true; }
        Require(failed && await f.CountAsync("SELECT count(*) FROM shop.bot_activity_events", ct) == 0, "deferred failure exposes no partial imported history");
        await using (var repair = f.Database.Data.CreateCommand("DROP TRIGGER synthetic_import_failure ON shop.bot_activity_events; DROP FUNCTION shop.synthetic_import_failure()")) await repair.ExecuteNonQueryAsync(ct);
        Require(await new TargetActivityImportWorkflow(f.Sessions).ImportAsync(batch, ct) == new TargetActivityImportResult(2, 0, 0), "new workflow instance resumes the exact original batch");
    }
    public static async Task BoundsAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: ct); await EnableAsync(f, ct); DateTime now = await Clock(f, ct);
        var row = new TargetActivityRow(Guid.NewGuid(), f.Users[0], BotActivityAction.Start, BotActivitySource.Command, BotActivityOutcome.Accepted, new(TelegramUpdate: 1000), now.AddMinutes(-1), TargetActivityOrigin.Telegram, Guid.Empty);
        bool oversized = false, future = false, unproven = false, wrong = false;
        try { Batch(f, now, Enumerable.Repeat(row, 501)); } catch (MigrationRejectedException) { oversized = true; }
        try { Batch(f, now, [row with { OccurredAt = now.AddSeconds(1) }]); } catch (MigrationRejectedException) { future = true; }
        try { await new TargetActivityImportWorkflow(f.Sessions).ImportAsync(Batch(f, now, [row]), ct); } catch (MigrationRejectedException) { unproven = true; }
        await using var other = await CheckoutFixture.CreateAsync(ct: ct);
        try { await new TargetActivityImportWorkflow(other.Sessions).ImportAsync(Batch(f, now, [row]), ct); } catch (MigrationRejectedException) { wrong = true; }
        Require(oversized && future && unproven && wrong && await f.CountAsync("SELECT count(*) FROM shop.bot_activity_events", ct) == 0 && await other.CountAsync("SELECT count(*) FROM shop.bot_activity_events", ct) == 0, "bounded input future clocks uncovered updates and wrong destinations fail closed");
        await using (var seed = await f.Sessions.BeginAsync(ct))
        {
            for (int i = 0; i < 500; i++) await TelegramReplayStore.TryClaimAsync(seed, f.Context(0), 1000 + i, ct);
            await seed.CommitAsync(ct);
        }
        now = await Clock(f, ct);
        var flood = Enumerable.Range(0, 500).Select(i => row with { Id = Guid.NewGuid(), Context = new(TelegramUpdate: 1000 + i), OccurredAt = now.AddHours(-1) }).ToArray();
        var batch = Batch(f, now, flood);
        Require(await new TargetActivityImportWorkflow(f.Sessions).ImportAsync(batch, ct) == new TargetActivityImportResult(500, 0, 0), "maximum bounded event flood imports once");
        Require(await new TargetActivityImportWorkflow(f.Sessions).ImportAsync(batch, ct) == new TargetActivityImportResult(0, 500, 0) && await f.CountAsync("SELECT count(*) FROM shop.bot_activity_events", ct) == 500, "full flood replay is bounded and deduplicated");

    }
    private static VerifiedTargetActivityBatch Batch(CheckoutFixture f, DateTime now, IEnumerable<TargetActivityRow> rows)
    {
        var destination = SchemaManifest.ForQualification(f.Database.Data.ConnectionString); var frozen = rows.ToArray();
        byte[] body = VerifiedTargetActivityBatch.Encode(destination, f.Brands[0], now, frozen);
        return VerifiedTargetActivityBatch.Open(destination, f.Brands[0], now, frozen, SHA256.HashData(body));
    }
    private static async Task EnableAsync(CheckoutFixture f, CancellationToken ct)
    {
        SchemaManifest.ForQualification(f.Database.Data.ConnectionString).Validate();
        await using var command = f.Database.Data.CreateCommand("DO $$ BEGIN CREATE ROLE shop_activity_import NOLOGIN; EXCEPTION WHEN duplicate_object OR unique_violation THEN NULL; END $$; GRANT shop_activity_import TO shop_test_runner");
        await command.ExecuteNonQueryAsync(ct);
    }
    private static async Task<DateTime> Clock(CheckoutFixture f, CancellationToken ct)
    { await using var read = await f.Sessions.BeginAsync(ct); return await read.QuerySingleAsync(new DbStatement("SELECT CURRENT_TIMESTAMP"), r => r.GetUtc(0), ct); }
    private static void Require(bool value, string stage) { if (!value) throw new InvalidOperationException("Target activity import qualification failed: " + stage + "."); }
}
