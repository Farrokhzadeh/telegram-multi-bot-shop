using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Platform.Migration;
using Platform.Persistence;
using Platform.Security;
namespace Platform.Testing;

public static class LegacyWalletOrderPlanQualification
{
    public static async Task RunAsync(string fault = "valid", CancellationToken ct = default)
    {
        using var files = new SyntheticSecretFiles(); using var secrets = SecretFiles.Load(files.Root, "service-config.json", files.Grant());
        var context = CipherQualification.Context(SyntheticSecretFiles.BrandA); Guid batch = Guid.NewGuid(); const string orderId = "ORD-ABC123ABC123", archiveHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        bool zeroCase = fault.StartsWith("zero_", StringComparison.Ordinal);
        byte[] register = Encoding.UTF8.GetBytes("""{"version":2,"policy":"manual_irr_and_original_nonposting_history_v2","sources":[{"source":"prod1","brand":"acc-abad","archive_sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","source_users":1,"wallets":[{"legacy_user_id":"101","original_irr_amount":"0"}],"history":[{"id":"ORD-ABC123ABC123","user_id":"101","amount":"-20","currency":"USD","created_at":"2024-02-01T00:00:01Z"}]},{"source":"prod2","brand":"kuala"},{"source":"prod3","brand":"zikotech"}]}""");
        if (zeroCase) register = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(register).Replace("\"-20\"", "\"0\"", StringComparison.Ordinal));
        var retention = LegacyWalletRetention.Open("protected:synthetic/order-retention", SHA256.HashData(register), register, "acc-abad", archiveHash, "101");
        DateTime first = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), last = first.AddMonths(2);
        var wallet = new LegacyWalletSnapshot("101", Guid.NewGuid(), Guid.NewGuid(), 101, "en", first, last, 0, "USD", 0, [new(orderId, Guid.NewGuid(), -20, "USD", new DateTime(2024, 2, 1, 0, 0, 1, DateTimeKind.Utc), null)], new(null, null, null, true, false, last), retention);
        string body = """{"id":"ORD-ABC123ABC123","user_id":101,"product_id":456,"qty":2,"total":"20","currency":"USD","created_at":"2024-02-01T00:00:00Z","checkout_snapshot":{"version":1,"product_id":456,"product_name":"Private original title","product_type":"credential","quantity":2,"original_usd":"20","total_usd":"20","original_toman":"600000","total_toman":"600000","savings_usd":"0","savings_toman":"0","discount_code":null,"discount_pct":null,"discount_quantity":0,"currency":"USD","payment_method":"wallet"}}""";
        if (zeroCase)
        {
            wallet = wallet with { History = [] }; var original = JsonNode.Parse(body)!; original["total"] = "0"; var snapshot = original["checkout_snapshot"]!;
            snapshot["total_usd"] = "0"; snapshot["total_toman"] = "0"; snapshot["savings_usd"] = "20"; snapshot["savings_toman"] = "600000"; snapshot["discount_code"] = "SYNTHETIC-FREE"; snapshot["discount_pct"] = "100"; snapshot["discount_quantity"] = 2; body = original.ToJsonString();
        }
        if (fault == "terms") body = body.Replace("\"quantity\":2", "\"quantity\":3", StringComparison.Ordinal);
        if (fault is "missing_snapshot" or "wallet_irr" or "nonterminating")
        {
            var original = JsonNode.Parse(body)!; var snapshot = original["checkout_snapshot"]!;
            if (fault == "missing_snapshot") original["checkout_snapshot"] = new JsonObject();
            else if (fault == "wallet_irr") { original["currency"] = "IRR"; original["total"] = "600000"; snapshot["currency"] = "IRR"; }
            else { original["currency"] = "IRR"; original["total"] = "600000"; original["qty"] = 3; snapshot["quantity"] = 3; snapshot["currency"] = "IRR"; snapshot["payment_method"] = "bank"; }
            body = original.ToJsonString();
        }
        if (fault == "owner") body = body.Replace("\"user_id\":101", "\"user_id\":102", StringComparison.Ordinal);
        if (fault == "debit") { body = body.Replace("\"20\"", "\"25\"", StringComparison.Ordinal); }
        if (fault == "method") body = body.Replace("\"payment_method\":\"wallet\"", "\"payment_method\":\"crypto\"", StringComparison.Ordinal);
        var source = new LegacySourceRecord(LegacyProjection.OrderRecord, orderId, body); Guid target = Guid.NewGuid();
        var destination = SchemaManifest.ForQualification("Host=127.0.0.1;Database=shop_qualification_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa;Username=shop_test_runner");
        VerifiedImportManifest Manifest(IEnumerable<LegacyMapProof> proofs, Guid id)
        {
            byte[] encoded = VerifiedImportManifest.EncodePlan(destination, "acc-abad", context.BrandId, "66371a23a1c8149dc5bdf89a0355aa689d454dba", SHA256.HashData("synthetic schema"u8), "protected:synthetic/frozen-copy", Convert.FromHexString(archiveHash), id, proofs);
            return VerifiedImportManifest.Open("protected:synthetic/order-plan", SHA256.HashData(encoded), encoded);
        }
        var walletManifest = Manifest(wallet.Proofs(context.BrandId), batch);
        var rawZero = zeroCase && fault != "zero_missing" ? new LegacySourceRecord(LegacyProjection.ZeroTransaction, orderId, """{"id":"ORD-ABC123ABC123","user_id":101,"amount":0,"currency":"USD","created_at":"2024-02-01T00:00:01Z"}""") : null; Guid zeroTarget = Guid.NewGuid();
        if (rawZero is not null)
        {
            var original = JsonNode.Parse(rawZero.Body)!;
            if (fault == "zero_owner") original["user_id"] = 102;
            if (fault == "zero_currency") original["currency"] = "IRR";
            if (fault == "zero_clock") original["created_at"] = "2024-02-01T00:00:02Z";
            rawZero = rawZero with { Body = original.ToJsonString() };
        }
        var originalProofs = wallet.Proofs(context.BrandId).Where(p => p.Projection == LegacyProjection.User).Append(source.Proof(target, context, secrets));
        var archive = Manifest(rawZero is null ? originalProofs : originalProofs.Append(rawZero.Proof(zeroTarget, context, secrets)), Guid.NewGuid());
        var row = await new LegacyRecordArchive(secrets).WriteAsync(context, archive, source, target, (_, _) => Task.FromResult("protected:synthetic/order-archive"), ct);
        LegacyWalletSnapshot[] wallets = [wallet]; ArchivedLegacyRecord[] records = fault == "missing" ? [] : fault == "duplicate" ? [row, row] : [row];
        if (rawZero is not null) records = [row, await new LegacyRecordArchive(secrets).WriteAsync(context, archive, rawZero, zeroTarget, (_, _) => Task.FromResult("protected:synthetic/original-zero"), ct)];
        if (zeroCase)
        {
            async Task<ArchivedLegacyRecord> Prepare(VerifiedImportManifest manifest, LegacySourceRecord original, Guid identity, CancellationToken token)
            {
                var prepared = await new LegacyRecordArchive(secrets).WriteAsync(context, manifest, original, identity, (_, _) => Task.FromResult("protected:synthetic/prepared-zero"), token);
                if (fault == "zero_prepared") { byte[] changed = prepared.Evidence.Ciphertext.ToArray(); changed[^1] ^= 1; changed.AsSpan().CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsMemory(prepared.Evidence.Ciphertext).Span); }
                return prepared;
            }
            Task<LegacyWalletOrderPlan> PlanZero() => LegacyWalletOrderPlan.CreateWithZeroOrdersAsync(context, secrets, archive, walletManifest, destination, "protected:synthetic/zero-orders", "Original synthetic brand", wallets, records, Prepare, ct);
            if (fault == "zero_prepared") { try { await PlanZero(); throw new InvalidOperationException("Changed prepared zero archive accepted."); } catch (MigrationRejectedException) { return; } }
            var zeroPlan = await PlanZero(); int accepted = fault == "zero_valid" ? 1 : 0;
            var zeroCoverage = LegacyRecordDispositionReport.Verify(zeroPlan);
            if (!zeroCoverage.Accounted || zeroCoverage.Archived != records.Length || zeroCoverage.Orders != accepted || zeroCoverage.NativeZero != accepted || zeroCoverage.Reviewed != records.Length - accepted * 2 || zeroCoverage.ReleaseReady || zeroCoverage.ToString().Contains(orderId, StringComparison.Ordinal)) throw new InvalidOperationException("Zero-order source disposition reconciliation failed.");
            try { LegacyRecordDispositionReport.Verify(zeroPlan.Records, zeroPlan.Set.Orders, zeroPlan.Set.Payments, zeroPlan.Set.Delivery, []); if (zeroCoverage.Reviewed != 0) throw new InvalidOperationException("Missing reviewed original accepted."); } catch (MigrationRejectedException) { }
            if (accepted != 0)
            {
                try { LegacyRecordDispositionReport.Verify(zeroPlan.Records, zeroPlan.Set.Orders, zeroPlan.Set.Payments, [], zeroPlan.Review); throw new InvalidOperationException("Missing original zero disposition accepted."); } catch (MigrationRejectedException) { }
                try { LegacyRecordDispositionReport.Verify(zeroPlan.Records, zeroPlan.Set.Orders.Concat(zeroPlan.Set.Orders).ToArray(), zeroPlan.Set.Payments, zeroPlan.Set.Delivery, zeroPlan.Review); throw new InvalidOperationException("Duplicated accepted order disposition accepted."); } catch (MigrationRejectedException) { }
            }
            if (zeroPlan.ReleaseReady || zeroPlan.Set.Orders.Count != accepted || zeroPlan.Set.Delivery.Count != accepted || zeroPlan.Review.Count + accepted * 2 != records.Length || zeroPlan.Manifest.Proofs.Count != walletManifest.Proofs.Count + accepted * 4 || zeroPlan.Set.Orders.Any(o => o.NativeWalletDebitAt is not null || o.NativeZeroSource is null) || zeroPlan.Set.Delivery.Any(r => r.Target != target || r.Source != rawZero)) throw new InvalidOperationException("Zero-order source binding or review coverage failed.");
            var secondZero = await PlanZero(); if (!zeroPlan.Manifest.Digest.Span.SequenceEqual(secondZero.Manifest.Digest.Span)) throw new InvalidOperationException("Zero-order plan replay changed.");
            return;
        }
        if (fault == "wallet") wallets = [wallet with { Language = "fa" }];
        if (fault == "retention") wallets = [wallet with { Retention = null }];
        if (fault == "cipher") { byte[] changed = row.Evidence.Ciphertext.ToArray(); changed[^1] ^= 1; changed.AsSpan().CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsMemory(row.Evidence.Ciphertext).Span); }
        if (fault is "missing" or "duplicate" or "wallet" or "retention" or "owner" or "cipher")
        {
            try { LegacyWalletOrderPlan.Create(context, secrets, archive, walletManifest, destination, "protected:synthetic/typed-orders", "Original synthetic brand", wallets, records); throw new InvalidOperationException("Unverified wallet order plan accepted."); }
            catch (MigrationRejectedException) { return; }
        }
        var plan = LegacyWalletOrderPlan.Create(context, secrets, archive, walletManifest, destination, "protected:synthetic/typed-orders", "Original synthetic brand", wallets, records);
        var coverage = LegacyRecordDispositionReport.Verify(plan);
        if (!coverage.Accounted || coverage.Archived != records.Length || coverage.Orders != plan.Set.Orders.Count || coverage.Reviewed != plan.Review.Count || coverage.ReleaseReady || coverage.ToString().Contains(orderId, StringComparison.Ordinal)) throw new InvalidOperationException("Wallet-order source disposition reconciliation failed.");
        if (plan.Review.Count != 0)
        {
            try { LegacyRecordDispositionReport.Verify(plan.Records, plan.Set.Orders, plan.Set.Payments, plan.Set.Delivery, []); throw new InvalidOperationException("Missing review disposition accepted."); } catch (MigrationRejectedException) { }
        }
        else
        {
            try { LegacyRecordDispositionReport.Verify(plan.Records, [], plan.Set.Payments, plan.Set.Delivery, plan.Review); throw new InvalidOperationException("Missing accepted order disposition accepted."); } catch (MigrationRejectedException) { }
        }
        int expected = fault == "valid" ? 1 : 0;
        if (plan.ReleaseReady || plan.Set.Orders.Count != expected || plan.Review.Count != 1 - expected || plan.Set.Orders.Count + plan.Review.Count != plan.Records.Count || plan.Manifest.Proofs.Count != walletManifest.Proofs.Count + 3 * expected || plan.Manifest.Batch != batch || plan.ToString().Contains(orderId, StringComparison.Ordinal)) throw new InvalidOperationException("Wallet order selection lost evidence or review accounting.");
        string reason = fault switch { "terms" => "incomplete_order_terms", "missing_snapshot" => "missing_checkout_snapshot", "wallet_irr" => "original_wallet_irr_terms", "nonterminating" => "nonterminating_unit_terms", "debit" => "missing_exact_native_wallet_debit", "method" => "outside_wallet_order_scope", _ => "" };
        if (expected == 0 && plan.Review[0].Reason != reason) throw new InvalidOperationException("Wallet order review reason changed.");
        if (expected == 1)
        {
            var order = plan.Set.Orders.Single();
            if (order.NativeWalletDebitAt != wallet.History.Single().OccurredAt) throw new InvalidOperationException("Original debit clock was replaced.");
            try { foreach (var proof in (order with { NativeWalletDebitAt = order.NativeWalletDebitAt!.Value.AddTicks(10) }).Proofs(context, secrets)) plan.Manifest.Require(proof); throw new InvalidOperationException("Changed debit clock accepted."); } catch (MigrationRejectedException) { }
        }
        var replay = LegacyWalletOrderPlan.Create(context, secrets, archive, walletManifest, destination, plan.Manifest.Reference, "Original synthetic brand", wallets, records);
        if (!plan.Manifest.Digest.Span.SequenceEqual(replay.Manifest.Digest.Span)) throw new InvalidOperationException("Wallet order plan replay changed.");
    }
}
