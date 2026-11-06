using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Platform.Migration;
using Platform.Persistence;
namespace Platform.Testing;

public static class WalletBatchQualification
{
    public static async Task RunAsync(string fault = "valid", CancellationToken ct = default)
    {
        await using var world = await LegacyWalletRetentionFixture.CreateAsync(ct); var f = world.Fixture;
        byte[] register = world.Register;
        if (fault is "clock" or "clock-source") { await using var change = f.Database.Data.CreateCommand("UPDATE public.users SET joined_at='2024-01-02 00:00Z',last_seen_at='2024-01-01 00:00Z'"); await change.ExecuteNonQueryAsync(ct); }
        if (fault == "rollback")
        {
            await using (var add = f.Database.Data.CreateCommand("INSERT INTO public.users VALUES(99001235,5.25,0,'fa','2024-01-01 00:00Z','2024-01-02 00:00Z',true,false)")) await add.ExecuteNonQueryAsync(ct);
            var json = JsonNode.Parse(register)!; json["sources"]![0]!["source_users"] = 2; json["sources"]![0]!["wallets"]!.AsArray().Add((JsonNode)new JsonObject { ["legacy_user_id"] = "99001235", ["original_irr_amount"] = "0" }); register = Encoding.UTF8.GetBytes(json.ToJsonString());
        }
        Task<ReadOnlyMemory<byte>> Read(string reference, CancellationToken token) => reference == "protected:synthetic/manual-wallet-history" ? Task.FromResult<ReadOnlyMemory<byte>>(register) : throw new KeyNotFoundException();
        byte[] schema = await world.Source.InspectSchemaAsync(ct); Guid batch = Guid.NewGuid(); DateTime observed = new(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var retained = LegacyWalletRetention.OpenAll("protected:synthetic/manual-wallet-history", SHA256.HashData(register), register, "acc-abad", LegacyWalletRetentionFixture.Archive);
        if (fault is "schema" or "missing")
        {
            try { await world.Source.PlanWalletsAsync(fault == "schema" ? new byte[32] : schema, batch, f.Brands[0], fault == "missing" ? new Dictionary<string, LegacyWalletRetention>() : retained, observed, ct); throw new InvalidOperationException("Unverified wallet plan accepted."); }
            catch (MigrationRejectedException) { return; }
        }
        var rows = await world.Source.PlanWalletsAsync(schema, batch, f.Brands[0], retained, observed, ct);
        if (fault is "clock" or "clock-source")
        {
            var row = rows.Single();
            if (row.FirstSeen != new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) || row.LastSeen != observed || row.SourceClock != new LegacyMembershipClock(observed, row.FirstSeen)) throw new InvalidOperationException("Original reversed membership clocks were not preserved.");
            var changed = row with { SourceClock = row.SourceClock with { JoinedAt = observed.AddTicks(10) } };
            try { changed.Validate(); throw new InvalidOperationException("Unbound membership clock was accepted."); } catch (MigrationRejectedException) { }
        }
        var second = await world.Source.PlanWalletsAsync(schema, batch, f.Brands[0], retained, observed, ct);
        byte[] Encode(IReadOnlyList<LegacyWalletSnapshot> source) => VerifiedImportManifest.EncodePlan(SchemaManifest.ForQualification(f.Database.Data.ConnectionString), "acc-abad", f.Brands[0], world.Manifest.SourceRevision, schema, world.Manifest.SnapshotReference, Convert.FromHexString(LegacyWalletRetentionFixture.Archive), batch, source.SelectMany(r => r.Proofs(f.Brands[0])), source.Sum(r => r.BalanceUsd));
        byte[] body = Encode(rows); if (!body.AsSpan().SequenceEqual(Encode(second))) throw new InvalidOperationException("Frozen wallet plan changed on replay.");
        var manifest = VerifiedImportManifest.Open("protected:synthetic/bulk-wallet-plan", SHA256.HashData(body), body);
        if (fault == "clock-source") { await using var change = f.Database.Data.CreateCommand("UPDATE public.users SET last_seen_at=last_seen_at+interval '1 second'"); await change.ExecuteNonQueryAsync(ct); }
        if (fault == "source") { await using var change = f.Database.Data.CreateCommand("UPDATE public.users SET balance=balance+1"); await change.ExecuteNonQueryAsync(ct); }
        if (fault == "namespace") rows = await world.Source.PlanWalletsAsync(schema, Guid.NewGuid(), f.Brands[0], retained, observed, ct);
        var workflow = new ImportWorkflow(world.Source, f.Sessions);
        if (fault is "source" or "namespace" or "register" or "clock-source")
        {
            try { await workflow.ImportWalletsAsync(f.Context(0), manifest, rows, fault == "register" ? (_, _) => Task.FromResult<ReadOnlyMemory<byte>>(new byte[128]) : Read, ct); throw new InvalidOperationException("Changed wallet batch accepted."); }
            catch (MigrationRejectedException) { if (await f.CountAsync("SELECT count(*) FROM shop.users", ct) != 0 || await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", ct) != 0) throw new InvalidOperationException("Rejected wallet batch wrote money."); return; }
        }
        if (fault == "rollback")
        {
            string owner = rows.Single(r => r.TelegramId == 99001235).TargetUser.ToString("D");
            await using (var trigger = f.Database.Data.CreateCommand("CREATE FUNCTION shop.reject_wallet_batch() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.user_id='" + owner + "'::uuid THEN RAISE EXCEPTION 'Public wallet batch commit failure' USING ERRCODE='23514'; END IF; RETURN NEW; END $$; CREATE CONSTRAINT TRIGGER reject_wallet_batch AFTER INSERT ON shop.wallet_entries DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION shop.reject_wallet_batch()")) await trigger.ExecuteNonQueryAsync(ct);
            try { await workflow.ImportWalletsAsync(f.Context(0), manifest, rows, Read, ct); throw new InvalidOperationException("Wallet batch commit fault was ignored."); }
            catch (DbOperationException) { if (await f.CountAsync("SELECT count(*) FROM shop.users", ct) != 1 || await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", ct) != 2) throw new InvalidOperationException("Wallet batch failed outside its membership transaction."); }
            await using var repair = f.Database.Data.CreateCommand("DROP TRIGGER reject_wallet_batch ON shop.wallet_entries"); await repair.ExecuteNonQueryAsync(ct);
        }
        var first = await workflow.ImportWalletsAsync(f.Context(0), manifest, rows, Read, ct);
        var replay = await workflow.ImportWalletsAsync(f.Context(0), manifest, rows, Read, ct);
        if (!first.Matched || first != replay || first.ComparedWallets != rows.Count || first.ManualIrrAccounts != 1 || first.RetainedHistoryRows != 3 || await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", ct) != (fault == "rollback" ? 3 : 2)) throw new InvalidOperationException("Bulk wallet import or reconciliation failed.");
    }
}
