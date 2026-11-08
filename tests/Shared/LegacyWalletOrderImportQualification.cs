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
        bool zeroOrder = fault.StartsWith("zero_", StringComparison.Ordinal); if (zeroOrder) fault = fault[5..];
        bool topup = fault.StartsWith("topup_", StringComparison.Ordinal); if (topup) fault = fault[6..];
        bool paidTopup = topup && fault == "paid"; if (paidTopup) fault = "valid";
        bool direct = fault.StartsWith("direct_", StringComparison.Ordinal); if (direct) fault = fault[7..];
        bool bank = direct && fault.StartsWith("bank_", StringComparison.Ordinal); if (bank) fault = fault[5..];
        if (direct && !bank) { if (!fault.StartsWith("crypto_", StringComparison.Ordinal)) throw new InvalidOperationException("Unknown direct-payment fixture."); fault = fault[7..]; }
        await using var world = await LegacyWalletRetentionFixture.CreateAsync(ct); var f = world.Fixture; var context = f.Context(0); const string reference = "ORD-ABC123ABC123";
        await using (var setup = f.Database.Data.CreateCommand("""
            CREATE TABLE public.orders(id text PRIMARY KEY,user_id bigint,product_id bigint,qty integer,total numeric,currency text,created_at timestamptz,checkout_snapshot jsonb);
            INSERT INTO public.orders VALUES('ORD-ABC123ABC123',99001234,456,2,20,'USD','2024-02-01 00:00Z','{"version":1,"product_id":456,"product_name":"Original synthetic title","product_type":"credential","quantity":2,"original_usd":"20","total_usd":"20","original_toman":"600000","total_toman":"600000","savings_usd":"0","savings_toman":"0","discount_code":null,"discount_pct":null,"discount_quantity":0,"currency":"USD","payment_method":"wallet"}');
            INSERT INTO public.transactions VALUES('ORD-ABC123ABC123',99001234,-20,'USD','2024-02-01 00:00:01Z');
            CREATE TABLE public.products(id bigint PRIMARY KEY,active boolean,is_removed boolean);
            CREATE TABLE public.inventory(id bigint PRIMARY KEY,order_id text);
            CREATE TABLE public.discounts(code text PRIMARY KEY);
            """)) await setup.ExecuteNonQueryAsync(ct);
        if (zeroOrder)
        {
            await using var change = f.Database.Data.CreateCommand("UPDATE public.orders SET total=0,checkout_snapshot=checkout_snapshot||'{\"total_usd\":\"0\",\"total_toman\":\"0\",\"savings_usd\":\"20\",\"savings_toman\":\"600000\",\"discount_code\":\"SYNTHETIC-FREE\",\"discount_pct\":\"100\",\"discount_quantity\":2}'::jsonb; UPDATE public.transactions SET amount=0 WHERE id='ORD-ABC123ABC123'"); await change.ExecuteNonQueryAsync(ct);
        }
        if (direct)
        {
            string currency = bank ? "IRR" : "USD", method = bank ? "bank" : "crypto";
            await using var change = f.Database.Data.CreateCommand("UPDATE public.orders SET currency='" + currency + "',total=" + (bank ? "600000" : "20") + ",checkout_snapshot=checkout_snapshot||'{\"currency\":\"" + currency + "\",\"payment_method\":\"" + method + "\"}'::jsonb; UPDATE public.transactions SET amount=0 WHERE id='ORD-ABC123ABC123'");
            await change.ExecuteNonQueryAsync(ct);
        }
        foreach (string table in new[] { "payment_attempts", "manual_deposits", "common_orderdelivery", "common_orderdeliverypart", "common_preorderdeliverybatch", "common_preorderdeliverybatchpart", "common_preorderdeliverybatch_items", "supplier_fulfillments", "supplier_fulfillment_items", "supplier_procurement_attempts", "supplier_refund_records", "preorder_order_states", "preorder_refund_records", "service_jobs", "common_deliveryattempt" })
        { await using var tableCommand = f.Database.Data.CreateCommand("CREATE TABLE public." + table + "(id text PRIMARY KEY)"); await tableCommand.ExecuteNonQueryAsync(ct); }
        if (topup)
        {
            await using var add = f.Database.Data.CreateCommand("""
                ALTER TABLE public.payment_attempts ADD COLUMN user_id bigint, ADD COLUMN expected_amount numeric, ADD COLUMN expected_currency text, ADD COLUMN kind text,
                    ADD COLUMN status text, ADD COLUMN pay_currency text, ADD COLUMN provider_payment_id text, ADD COLUMN provider_status text, ADD COLUMN paid_at timestamptz,
                    ADD COLUMN created_at timestamptz, ADD COLUMN fulfilled_order_id text;
                INSERT INTO public.payment_attempts(id,user_id,expected_amount,expected_currency,kind,status,pay_currency,provider_payment_id,paid_at,created_at)
                VALUES('SYNTHETIC-TOPUP',99001234,20,'USD','wallet_topup','invoice_created','usdttrc20',NULL,NULL,'2024-02-01 00:00:02Z');
                """);
            await add.ExecuteNonQueryAsync(ct);
            if (paidTopup) { await using var paid = f.Database.Data.CreateCommand("UPDATE public.payment_attempts SET status='fulfilled',provider_payment_id='synthetic-original-provider',paid_at='2024-02-01 00:00:03Z'"); await paid.ExecuteNonQueryAsync(ct); }
        }
        if (direct)
        {
            string sql = bank ? """
                ALTER TABLE public.manual_deposits ADD COLUMN user_id bigint, ADD COLUMN amount numeric, ADD COLUMN status text, ADD COLUMN reviewed_at timestamptz, ADD COLUMN created_at timestamptz, ADD COLUMN fulfilled_order_id text;
                INSERT INTO public.manual_deposits VALUES('SYNTHETIC-DIRECT',99001234,600000,'approved','2024-02-01 00:00:03Z','2024-02-01 00:00:02Z','ORD-ABC123ABC123');
                """ : """
                ALTER TABLE public.payment_attempts ADD COLUMN user_id bigint, ADD COLUMN expected_amount numeric, ADD COLUMN expected_currency text, ADD COLUMN kind text,
                    ADD COLUMN status text, ADD COLUMN pay_currency text, ADD COLUMN provider_payment_id text, ADD COLUMN provider_status text, ADD COLUMN paid_at timestamptz,
                    ADD COLUMN created_at timestamptz, ADD COLUMN fulfilled_order_id text;
                INSERT INTO public.payment_attempts VALUES('SYNTHETIC-DIRECT',99001234,20,'USD','direct_checkout','fulfilled','usdttrc20','synthetic-provider',NULL,'2024-02-01 00:00:03Z','2024-02-01 00:00:02Z','ORD-ABC123ABC123');
                """;
            await using var add = f.Database.Data.CreateCommand(sql); await add.ExecuteNonQueryAsync(ct);
            string table = bank ? "manual_deposits" : "payment_attempts";
            if (fault is "owner" or "amount" or "status")
            {
                string value = fault switch { "owner" => "user_id=99001235", "amount" => (bank ? "amount=600001" : "expected_amount=21"), _ => bank ? "status='denied'" : "status='failed'" };
                await using var change = f.Database.Data.CreateCommand("UPDATE public." + table + " SET " + value); await change.ExecuteNonQueryAsync(ct);
            }
        }
        var json = JsonNode.Parse(world.Register)!;
        json["sources"]![0]!["history"]!.AsArray().Add(JsonNode.Parse("""{"id":"ORD-ABC123ABC123","user_id":"99001234","amount":"-20","currency":"USD","created_at":"2024-02-01T00:00:01Z"}""")!);
        if (zeroOrder || direct) json["sources"]![0]!["history"]!.AsArray()[^1]!["amount"] = "0";
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
        await using (var read = f.Database.Data.CreateCommand("SELECT to_jsonb(s)::text FROM public.transactions s WHERE id='zero-original'")) body = (string)(await read.ExecuteScalarAsync(ct))!;
        var zero = new LegacySourceRecord(LegacyProjection.ZeroTransaction, "zero-original", body);
        LegacySourceRecord? acceptance = null; Guid acceptanceTarget = Guid.NewGuid();
        if (zeroOrder) { await using var read = f.Database.Data.CreateCommand("SELECT to_jsonb(s)::text FROM public.transactions s WHERE id='ORD-ABC123ABC123'"); acceptance = new(LegacyProjection.ZeroTransaction, reference, (string)(await read.ExecuteScalarAsync(ct))!); }
        LegacySourceRecord? topupSource = null; Guid topupTarget = Guid.NewGuid();
        if (topup) { await using var read = f.Database.Data.CreateCommand("SELECT to_jsonb(s)::text FROM public.payment_attempts s"); topupSource = new(LegacyProjection.PaymentAttempt, "SYNTHETIC-TOPUP", (string)(await read.ExecuteScalarAsync(ct))!); }
        var originalProofs = walletManifest.Proofs.Where(p => p.Projection == LegacyProjection.User).Append(source.Proof(target, context, f.Secrets)).Append(zero.Proof(zeroTarget, context, f.Secrets));
        var proofs = acceptance is null ? originalProofs : originalProofs.Append(acceptance.Proof(acceptanceTarget, context, f.Secrets));
        LegacySourceRecord? directSource = null; Guid directTarget = Guid.NewGuid();
        if (direct)
        {
            await using var read = f.Database.Data.CreateCommand("SELECT to_jsonb(s)::text FROM public." + (bank ? "manual_deposits" : "payment_attempts") + " s");
            directSource = new(bank ? LegacyProjection.ManualDeposit : LegacyProjection.PaymentAttempt, "SYNTHETIC-DIRECT", (string)(await read.ExecuteScalarAsync(ct))!);
        }
        var covered = topupSource is null ? proofs : proofs.Append(topupSource.Proof(topupTarget, context, f.Secrets));
        var archive = Manifest(directSource is null ? covered : covered.Append(directSource.Proof(directTarget, context, f.Secrets)), Guid.NewGuid());
        ProtectedLegacyEvidence? evidence = null;
        var row = await new LegacyRecordArchive(f.Secrets).WriteAsync(context, archive, source, target, (value, _) => { evidence = value; return Task.FromResult("protected:synthetic/wallet-order-evidence"); }, ct);
        ProtectedLegacyEvidence? zeroEvidence = null;
        var zeroRow = await new LegacyRecordArchive(f.Secrets).WriteAsync(context, archive, zero, zeroTarget, (value, _) => { zeroEvidence = value; return Task.FromResult("protected:synthetic/zero-evidence"); }, ct);
        var protectedFiles = new Dictionary<string, ProtectedLegacyEvidence>(StringComparer.Ordinal) { [row.Reference] = evidence!, [zeroRow.Reference] = zeroEvidence! }; var originals = new List<ArchivedLegacyRecord> { row, zeroRow };
        if (acceptance is not null) originals.Add(await new LegacyRecordArchive(f.Secrets).WriteAsync(context, archive, acceptance, acceptanceTarget, (value, _) => { protectedFiles["protected:synthetic/native-zero"] = value; return Task.FromResult("protected:synthetic/native-zero"); }, ct));
        if (topupSource is not null) originals.Add(await new LegacyRecordArchive(f.Secrets).WriteAsync(context, archive, topupSource, topupTarget, (value, _) => { protectedFiles["protected:synthetic/topup"] = value; return Task.FromResult("protected:synthetic/topup"); }, ct));
        if (directSource is not null) originals.Add(await new LegacyRecordArchive(f.Secrets).WriteAsync(context, archive, directSource, directTarget, (value, _) => { protectedFiles["protected:synthetic/direct-payment"] = value; return Task.FromResult("protected:synthetic/direct-payment"); }, ct));
        Task<ArchivedLegacyRecord> Prepare(VerifiedImportManifest manifest, LegacySourceRecord original, Guid identity, CancellationToken token) => new LegacyRecordArchive(f.Secrets).WriteAsync(context, manifest, original, identity, (value, _) => { protectedFiles["protected:synthetic/rebound-zero"] = value; return Task.FromResult("protected:synthetic/rebound-zero"); }, token);
        var plan = zeroOrder || topup || direct ? await LegacyWalletOrderPlan.CreateWithZeroOrdersAsync(context, f.Secrets, archive, walletManifest, destination, "protected:synthetic/zero-orders", "Original synthetic brand", wallets, originals, Prepare, ct, topup, direct) : LegacyWalletOrderPlan.Create(context, f.Secrets, archive, walletManifest, destination, "protected:synthetic/wallet-orders", "Original synthetic brand", wallets, originals);
        bool heldDirect = direct && fault is "owner" or "amount" or "status";
        if (plan.Set.Orders.Count != (heldDirect ? 0 : 1) || plan.Review.Count != (heldDirect ? 3 : 1) || plan.Set.Delivery.Count != (zeroOrder ? 1 : 0) || plan.Set.Payments.Count != (topup || direct && !heldDirect ? 1 : 0)) throw new InvalidOperationException("Typed wallet order fixture was not selected.");
        var disposition = LegacyRecordDispositionReport.Verify(plan);
        if (!disposition.Accounted || disposition.Archived != originals.Count || disposition.Reviewed != plan.Review.Count) throw new InvalidOperationException("Direct-payment source disposition incomplete.");
        if (heldDirect)
        {
            var held = await new ImportWorkflow(world.Source, f.Sessions).ImportWalletOrdersAsync(context, f.Secrets, plan, (reference, _) => Task.FromResult(protectedFiles[reference]), ReadRetention, new LegacyRecordReconciliationReport(f.Database.Data, f.Secrets), ct);
            if (!held.Matched || await f.CountAsync("SELECT count(*) FROM shop.orders", ct) != 0 || await f.CountAsync("SELECT count(*) FROM shop.payments", ct) != 0) throw new InvalidOperationException("Unproven direct payment was imported."); return;
        }
        Task<ProtectedLegacyEvidence> ReadArchive(string reference, CancellationToken token) => Task.FromResult(protectedFiles[reference]);
        var workflow = new ImportWorkflow(world.Source, f.Sessions); var reports = new LegacyRecordReconciliationReport(f.Database.Data, f.Secrets);
        Task<LegacyRecordReconciliation> Import() => workflow.ImportWalletOrdersAsync(context, f.Secrets, plan, ReadArchive, ReadRetention, reports, ct);
        if (fault == "source") { await using var change = f.Database.Data.CreateCommand("UPDATE public.orders SET total=21"); await change.ExecuteNonQueryAsync(ct); }
        if (fault == "unselected") { await using var change = f.Database.Data.CreateCommand("INSERT INTO public.discounts VALUES('UNPLANNED-PRIVATE-CODE')"); await change.ExecuteNonQueryAsync(ct); }
        if (fault == "wallet") { await using var change = f.Database.Data.CreateCommand("UPDATE public.users SET balance=balance+1"); await change.ExecuteNonQueryAsync(ct); }
        if (fault == "register") register[^1] ^= 1;
        if (fault == "archive") protectedFiles[row.Reference] = evidence! with { Ciphertext = new byte[evidence!.Ciphertext.Length] };
        if (direct && fault == "source") { await using var changed = f.Database.Data.CreateCommand("UPDATE public." + (bank ? "manual_deposits" : "payment_attempts") + " SET " + (bank ? "amount=600001" : "expected_amount=21")); await changed.ExecuteNonQueryAsync(ct); }
        if (direct && fault == "tampered") protectedFiles["protected:synthetic/direct-payment"] = protectedFiles["protected:synthetic/direct-payment"] with { Ciphertext = new byte[protectedFiles["protected:synthetic/direct-payment"].Ciphertext.Length] };
        if (fault == "payment") { await using var change = f.Database.Data.CreateCommand("UPDATE public.payment_attempts SET expected_amount=21"); await change.ExecuteNonQueryAsync(ct); }
        if (fault == "tampered") protectedFiles["protected:synthetic/topup"] = protectedFiles["protected:synthetic/topup"] with { Ciphertext = new byte[protectedFiles["protected:synthetic/topup"].Ciphertext.Length] };
        if (fault == "prepared") { var value = protectedFiles["protected:synthetic/rebound-zero"]; protectedFiles["protected:synthetic/rebound-zero"] = value with { Ciphertext = new byte[value.Ciphertext.Length] }; }
        if (fault != "valid")
        {
            try { await Import(); throw new InvalidOperationException("Changed wallet order input accepted."); }
            catch (MigrationRejectedException)
            {
                if (await f.CountAsync("SELECT count(*) FROM shop.users", ct) != 0 || await f.CountAsync("SELECT count(*) FROM shop.orders", ct) != 0 || await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", ct) != 0) throw new InvalidOperationException("Rejected preflight wrote target state."); return;
            }
        }
        var first = await Import(); var replay = await Import();
        if (!first.Matched || first != replay || first.ReleaseReady || first.ComparedRecords != (zeroOrder ? 3 : 2) + (topup ? 1 : 0) || await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='" + (zeroOrder ? "not_required" : "paid") + "' AND fulfillment_status='requires_admin' AND review_code='credential_recovery' AND payment_confirmed_at='2024-02-01 00:00:01Z' AND created_at='2024-02-01 00:00Z'", ct) != 1 || await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE is_posting", ct) != 1 || await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", ct) != (zeroOrder ? 2 : 3) || await f.CountAsync("SELECT count(*) FROM shop.jobs", ct) != 0 || await f.CountAsync("SELECT count(*) FROM shop.payments WHERE legacy_kind='payment_attempt' AND status='" + (paidTopup ? "settled" : "awaiting") + "'", ct) != (topup ? 1 : 0)) throw new InvalidOperationException("Typed wallet order import or replay changed financial state.");
    }
}
