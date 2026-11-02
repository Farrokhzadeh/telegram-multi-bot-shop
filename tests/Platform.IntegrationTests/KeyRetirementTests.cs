using System.Security.Cryptography;
using System.Text.Json;
using Platform.Migration;

namespace Platform.IntegrationTests;

public sealed class KeyRetirementTests
{
    [Fact]
    public void ProtectedManifestRetainsEqualityUnboundedHistoryAndRollbackDependenciesWithoutDeleteAuthority()
    {
        var now = new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);
        var dependency = new BackupKeyDependency("protected:synthetic/backup", Convert.ToHexStringLower(SHA256.HashData("public backup"u8)), now.AddDays(-1), true, false, false, true);
        var plan = new KeyDependencyPlan(Guid.NewGuid(), "protected:synthetic/old-key-handle", "protected:synthetic/complete-inventory-attestation", true, [dependency]);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(plan, KeyDependencyJsonContext.Default.KeyDependencyPlan);
        var verified = KeyRetirementGate.Open("protected:synthetic/dependencies", SHA256.HashData(bytes), bytes);
        Assert.True(KeyRetirementGate.Evaluate(verified, now).EligibleForSeparateReview);
        Assert.False(KeyRetirementGate.Evaluate(verified, now).MayDelete);
        Assert.Equal(KeyRetirementReason.InventoryIncomplete, KeyRetirementGate.Evaluate(plan with { InventoryAttested = false }, now).Reason);
        foreach (var changed in new[] { dependency with { RetainUntilUtc = now }, dependency with { RetainUntilUtc = null }, dependency with { RollbackRequired = true }, dependency with { HistoryRequired = true }, dependency with { RestoreVerified = false }, dependency with { ReencryptedVerified = false } })
            Assert.False(KeyRetirementGate.Evaluate(plan with { Dependencies = [changed] }, now).EligibleForSeparateReview);
        bytes[^1] ^= 1;
        Assert.Throws<MigrationRejectedException>(() => KeyRetirementGate.Open("protected:synthetic/dependencies", SHA256.HashData("another public digest"u8), bytes));
        Assert.DoesNotContain("old-key-handle", plan.ToString());
    }
}
