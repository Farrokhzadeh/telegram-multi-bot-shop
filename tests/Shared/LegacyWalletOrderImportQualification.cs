using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Platform.Migration;
using Platform.Persistence;
namespace Platform.Testing;

public static class LegacyWalletOrderImportQualification
{
    public static async Task RunAsync(string fault = "valid", CancellationToken ct = default)
    {
        await using var world = await LegacyWalletRetentionFixture.CreateAsync(ct); var f = world.Fixture; var context = f.Context(0); const string reference = "ORD-ABC123ABC123";
        await using (var setup = f.Database.Data.CreateCommand("""
            CREATE TABLE public.orders(id text PRIMARY KEY,user_id bigint,product_id bigint,qty integer,total numeric,currency text,created_at timestamptz,checkout_snapshot jsonb);
            INSERT INTO public.orders VALUES('ORD-ABC123ABC123',99001234,456,2,20,'USD','2024-02-01 00:00Z','{"version":1,"product_id":456,"product_name":"Original synthetic title","product_type":"credential","quantity":2,"original_usd":"20","total_usd":"20","original_toman":"600000","total_toman":"600000","savings_usd":"0","savings_toman":"0","discount_code":null,"discount_pct":null,"discount_quantity":0,"currency":"USD","payment_method":"wallet"}');
            INSERT INTO public.transactions VALUES('ORD-ABC123ABC123',99001234,-20,'USD','2024-02-01 00:00:01Z');
            CREATE TABLE public.products(id bigint PRIMARY KEY,active boolean,is_removed boolean);
            CREATE TABLE public.inventory(id bigint PRIMARY KEY,order_id text);
            CREATE TABLE public.discounts(code text PRIMARY KEY);
            """)) await setup.ExecuteNonQueryAsync(ct);
        foreach (string table in new[] { "payment_attempts", "manual_deposits", "common_orderdelivery", "common_orderdeliverypart", "common_preorderdeliverybatch", "common_preorderdeliverybatchpart", "common_preorderdeliverybatch_items", "supplier_fulfillments", "supplier_fulfillment_items", "supplier_procurement_attempts", "supplier_refund_records", "preorder_order_states", "preorder_refund_records", "service_jobs", "common_deliveryattempt" })
        { await using var tableCommand = f.Database.Data.CreateCommand("CREATE TABLE public." + table + "(id text PRIMARY KEY)"); await tableCommand.ExecuteNonQueryAsync(ct); }
        var json = JsonNode.Parse(world.Register)!;
        json["sources"]![0]!["history"]!.AsArray().Add(JsonNode.Parse("""{"id":"ORD-ABC123ABC123","user_id":"99001234","amount":"-20","currency":"USD","created_at":"2024-02-01T00:00:01Z"}""")!);
        byte[] register = Encoding.UTF8.GetBytes(json.ToJsonString());
        Task<ReadOnlyMemory<byte>> ReadRetention(string _, CancellationToken token) => Task.FromResult<ReadOnlyMemory<byte>>(register);
        var retained = LegacyWalletRetention.OpenAll("protected:synthetic/manual-wallet-history", SHA256.HashData(register), register, "acc-abad", LegacyWalletRetentionFixture.Archive);
        byte[] schema = await world.Source.InspectSchemaAsync(ct); Guid batch = Guid.NewGuid();
        var wallets = await world.Source.PlanWalletsAsync(schema, batch, context.BrandId, retained, new DateTime(2024, 2, 2, 0, 0, 0, DateTimeKind.Utc), ct);
        var destination = SchemaManifest.ForQualification(f.Database.Data.ConnectionString);
        VerifiedImportManifest Manifest(IEnumerable<LegacyMapProof> proofs, Guid id)
        {
            byte[] body = VerifiedImportManifest.EncodePlan(destination, "acc-abad", context.BrandId, world.Manifest.SourceRevision, schema, world.Manifest.SnapshotReference, Convert.FromHexString(LegacyWalletRetentionFixture.Archive), id, proofs, wallets.Sum(w => w.BalanceUsd));
            return VerifiedImportManifest.Open("protected:synthetic/wallet-order-input", SHA256.HashData(body), body);
        }
        var walletManifest = Manifest(wallets.SelectMany(w => w.Proofs(context.BrandId)), batch); string body;
        await using (var read = f.Database.Data.CreateCommand("SELECT to_jsonb(s)::text FROM public.orders s")) body = (string)(await read.ExecuteScalarAsync(ct))!;
        var source = new LegacySourceRecord(LegacyProjection.OrderRecord, reference, body); Guid target = Guid.NewGuid(), zeroTarget = Guid.NewGuid();
        await using (var read = f.Database.Data.CreateCommand("SELECT to_jsonb(s)::text FROM public.transactions s WHERE amount=0")) body = (string)(await read.ExecuteScalarAsync(ct))!;
        var zero = new LegacySourceRecord(LegacyProjection.ZeroTransaction, "zero-original", body);
        var archive = Manifest(walletManifest.Proofs.Where(p => p.Projection == LegacyProjection.User).Append(source.Proof(target, context, f.Secrets)).Append(zero.Proof(zeroTarget, context, f.Secrets)), Guid.NewGuid());
        ProtectedLegacyEvidence? evidence = null;
        var row = await new LegacyRecordArchive(f.Secrets).WriteAsync(context, archive, source, target, (value, _) => { evidence = value; return Task.FromResult("protected:synthetic/wallet-order-evidence"); }, ct);
        ProtectedLegacyEvidence? zeroEvidence = null;
        var zeroRow = await new LegacyRecordArchive(f.Secrets).WriteAsync(context, archive, zero, zeroTarget, (value, _) => { zeroEvidence = value; return Task.FromResult("protected:synthetic/zero-evidence"); }, ct);
        var plan = LegacyWalletOrderPlan.Create(context, f.Secrets, archive, walletManifest, destination, "protected:synthetic/wallet-orders", "Original synthetic brand", wallets, [row, zeroRow]);
        if (plan.Set.Orders.Count != 1 || plan.Review.Count != 1) throw new InvalidOperationException("Typed wallet order fixture was not selected.");
        Task<ProtectedLegacyEvidence> ReadArchive(string _, CancellationToken token) => Task.FromResult(_ == row.Reference ? evidence! : zeroEvidence!);
        var workflow = new ImportWorkflow(world.Source, f.Sessions); var reports = new LegacyRecordReconciliationReport(f.Database.Data, f.Secrets);
        Task<LegacyRecordReconciliation> Import() => workflow.ImportWalletOrdersAsync(context, f.Secrets, plan, ReadArchive, ReadRetention, reports, ct);
        if (fault == "source") { await using var change = f.Database.Data.CreateCommand("UPDATE public.orders SET total=21"); await change.ExecuteNonQueryAsync(ct); }
        if (fault == "unselected") { await using var change = f.Database.Data.CreateCommand("INSERT INTO public.discounts VALUES('UNPLANNED-PRIVATE-CODE')"); await change.ExecuteNonQueryAsync(ct); }
        if (fault == "wallet") { await using var change = f.Database.Data.CreateCommand("UPDATE public.users SET balance=balance+1"); await change.ExecuteNonQueryAsync(ct); }
        if (fault == "register") register[^1] ^= 1;
        if (fault == "archive") evidence = evidence! with { Ciphertext = new byte[evidence!.Ciphertext.Length] };
        if (fault != "valid")
        {
            try { await Import(); throw new InvalidOperationException("Changed wallet order input accepted."); }
            catch (MigrationRejectedException)
            {
                if (await f.CountAsync("SELECT count(*) FROM shop.users", ct) != 0 || await f.CountAsync("SELECT count(*) FROM shop.orders", ct) != 0 || await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", ct) != 0) throw new InvalidOperationException("Rejected preflight wrote target state."); return;
            }
        }
        var first = await Import(); var replay = await Import();
        if (!first.Matched || first != replay || first.ReleaseReady || first.ComparedRecords != 2 || await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='paid' AND fulfillment_status='requires_admin' AND review_code='credential_recovery' AND payment_confirmed_at='2024-02-01 00:00:01Z' AND created_at='2024-02-01 00:00Z'", ct) != 1 || await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE is_posting", ct) != 1 || await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", ct) != 3 || await f.CountAsync("SELECT count(*) FROM shop.jobs", ct) != 0) throw new InvalidOperationException("Typed wallet order import or replay changed financial state.");
    }
}
