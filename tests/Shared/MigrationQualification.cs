using System.Security.Cryptography;
using System.Text;
using Platform.Migration;
using Platform.Persistence;

namespace Platform.Testing;

public static class MigrationQualification
{
    public static LegacyWalletSnapshot Wallet() => new("source-member-1", Guid.NewGuid(), Guid.NewGuid(), 99001234, "en", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), 12.34m, "USD", 0,
        [new("source-entry-1", Guid.NewGuid(), 100, "USD", new DateTime(2024, 1, 1, 1, 0, 0, DateTimeKind.Utc), 100), new("source-entry-2", Guid.NewGuid(), -10, "USD", new DateTime(2024, 1, 1, 2, 0, 0, DateTimeKind.Utc), 90)]);
    public static VerifiedImportManifest Manifest(CheckoutFixture f, string source, Guid brand, LegacyWalletSnapshot[] rows, LegacyMapProof[]? additional = null)
    {
        var proofs = rows.SelectMany(r => r.Proofs(brand)).Concat(additional ?? []).ToArray();
        byte[] body = VerifiedImportManifest.EncodePlan(SchemaManifest.ForQualification(f.Database.Data.ConnectionString), source, brand, "7fcc4a7a35337405dd69195fc6c92474748c4513", SHA256.HashData("public synthetic schema contract"u8), "protected:synthetic/frozen-source", SHA256.HashData("public synthetic frozen source"u8), Guid.NewGuid(), proofs, rows.Sum(r => r.BalanceUsd));
        byte[] digest = SHA256.HashData(body);
        return VerifiedImportManifest.Open("protected:synthetic/manifests/" + Convert.ToHexStringLower(digest), digest, body);
    }
    public static async Task EnableRoleAsync(CheckoutFixture f, CancellationToken ct)
    {
        SchemaManifest.ForQualification(f.Database.Data.ConnectionString).Validate();
        await using var command = f.Database.Data.CreateCommand("DO $$ BEGIN CREATE ROLE shop_legacy_import NOLOGIN; EXCEPTION WHEN duplicate_object OR unique_violation THEN NULL; END $$");
        await command.ExecuteNonQueryAsync(ct);
        await using var membership = f.Database.Data.CreateCommand("GRANT shop_legacy_import TO shop_test_runner");
        await membership.ExecuteNonQueryAsync(ct);
    }
    public static async Task RunAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(users: 0, balance: 0, stock: 0, ct: ct);
        await EnableRoleAsync(f, ct);
        var row = Wallet(); var manifest = Manifest(f, "public-a", f.Brands[0], [row]);
        var writer = new OpeningBalanceImportWorkflow(f.Sessions);
        await writer.ImportAsync(f.Context(0), manifest, row, ct);
        await writer.ImportAsync(f.Context(0), manifest, row, ct);
        if (await f.CountAsync("SELECT count(*) FROM shop.wallets WHERE balance=12.34 AND version=1", ct) != 1 || await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='opening_balance' AND is_posting", ct) != 1 ||
            await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='legacy_history' AND NOT is_posting", ct) != 2) throw new InvalidOperationException("Frozen opening/non-posting history qualification failed.");
        var report = await new ReconciliationReport(f.Sessions).ReadAsync(manifest, ct);
        if (!report.Complete || report.OpeningUsd != 12.34m || report.NonPostingHistory != 2 || report.SaleableInventory != 0) throw new InvalidOperationException("Migration reconciliation qualification failed.");
        using (var vectors = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "legacy-fernet-synthetic.json"))))
        {
            var vector = vectors.RootElement.GetProperty("vectors").EnumerateArray().First(v => v.GetProperty("kind").GetString() == "Secret");
            string[] keys = vector.GetProperty("keys").EnumerateArray().Select(v => v.GetString()!).ToArray();
            var protectedValue = new ReencryptionWorkflow(f.Secrets).Brand(f.Context(0), Guid.NewGuid(), MigrationCipherField.FulfillmentContent, vector.GetProperty("ciphertext").GetString()!, LegacyFieldKind.Secret, keys);
            if (protectedValue.KeyId != "current") throw new InvalidOperationException("Native migration re-encryption key rejected.");
        }
        var now = DateTime.UtcNow;
        var keyPlan = new KeyDependencyPlan(Guid.NewGuid(), "protected:synthetic/old-key-handle", "protected:synthetic/complete-inventory", true,
            [new("protected:synthetic/backup", Convert.ToHexStringLower(SHA256.HashData("public synthetic backup"u8)), now.AddDays(-1), true, false, false, true)]);
        byte[] keyBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(keyPlan, KeyDependencyJsonContext.Default.KeyDependencyPlan);
        var assessment = KeyRetirementGate.Evaluate(KeyRetirementGate.Open("protected:synthetic/key-plan", SHA256.HashData(keyBytes), keyBytes), now);
        if (!assessment.EligibleForSeparateReview || assessment.MayDelete) throw new InvalidOperationException("Native key retirement gate rejected.");
        Directory.CreateDirectory("artifacts/migration");
        var redacted = ReconciliationReport.Redact(report);
        if (!redacted.WalletAmountsMatched || !redacted.WalletProjectionMatched || redacted.FinancialObligationsVerified || redacted.ReleaseReady) throw new InvalidOperationException("Redacted migration reconciliation qualification failed.");
        byte[] bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(redacted, MigrationReportJsonContext.Default.RedactedMigrationReconciliation);
        await File.WriteAllBytesAsync("artifacts/migration/synthetic-reconciliation.json", bytes, ct);
        Console.WriteLine(Convert.ToHexStringLower(SHA256.HashData(bytes)) + "  artifacts/migration/synthetic-reconciliation.json");
    }
}
