using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Platform.Migration;

public sealed record BackupKeyDependency(string ArtifactReference, string Sha256, DateTime? RetainUntilUtc, bool RestoreVerified, bool RollbackRequired, bool HistoryRequired, bool ReencryptedVerified)
{
    public override string ToString() => "BackupKeyDependency [redacted]";
}
public sealed record KeyDependencyPlan(Guid Id, string OldKeyReference, string CompleteInventoryEvidence, bool InventoryAttested, BackupKeyDependency[] Dependencies)
{
    public override string ToString() => "KeyDependencyPlan [redacted]";
}
public enum KeyRetirementReason { InventoryIncomplete, UnboundedRetention, RetentionActive, RestoreUnverified, RollbackRequired, HistoryRequired, ReencryptionUnverified, EligibleForSeparateReview }
public sealed record KeyRetirementAssessment(Guid PlanId, int Dependencies, bool EligibleForSeparateReview, KeyRetirementReason Reason)
{
    public bool MayDelete => false;
}
public static class KeyRetirementGate
{
    public static KeyDependencyPlan Open(string protectedReference, ReadOnlySpan<byte> pinnedDigest, ReadOnlySpan<byte> bytes)
    {
        Reference(protectedReference);
        if (pinnedDigest.Length != 32 || bytes.Length is < 2 or > 1048576 || !CryptographicOperations.FixedTimeEquals(pinnedDigest, SHA256.HashData(bytes))) throw new MigrationRejectedException();
        try
        {
            var plan = JsonSerializer.Deserialize(bytes, KeyDependencyJsonContext.Default.KeyDependencyPlan) ?? throw new MigrationRejectedException();
            Validate(plan); return plan;
        }
        catch (JsonException) { throw new MigrationRejectedException(); }
    }
    public static KeyRetirementAssessment Evaluate(KeyDependencyPlan plan, DateTime now)
    {
        Validate(plan);
        if (now.Kind != DateTimeKind.Utc || now == DateTime.MinValue || now == DateTime.MaxValue) throw new MigrationRejectedException();
        KeyRetirementReason reason = !plan.InventoryAttested ? KeyRetirementReason.InventoryIncomplete :
            plan.Dependencies.Any(d => d.RollbackRequired) ? KeyRetirementReason.RollbackRequired :
            plan.Dependencies.Any(d => d.HistoryRequired) ? KeyRetirementReason.HistoryRequired :
            plan.Dependencies.Any(d => !d.ReencryptedVerified) ? KeyRetirementReason.ReencryptionUnverified :
            plan.Dependencies.Any(d => !d.RestoreVerified) ? KeyRetirementReason.RestoreUnverified :
            plan.Dependencies.Any(d => d.RetainUntilUtc is null) ? KeyRetirementReason.UnboundedRetention :
            plan.Dependencies.Any(d => d.RetainUntilUtc >= now) ? KeyRetirementReason.RetentionActive : KeyRetirementReason.EligibleForSeparateReview;
        return new(plan.Id, plan.Dependencies.Length, reason == KeyRetirementReason.EligibleForSeparateReview, reason);
    }
    private static void Validate(KeyDependencyPlan plan)
    {
        if (plan.Id == Guid.Empty || plan.Dependencies.Length > 100000 || plan.Dependencies.Select(d => d.ArtifactReference).Distinct(StringComparer.Ordinal).Count() != plan.Dependencies.Length) throw new MigrationRejectedException();
        Reference(plan.OldKeyReference); Reference(plan.CompleteInventoryEvidence);
        foreach (var d in plan.Dependencies)
        { Reference(d.ArtifactReference); if (d.Sha256.Length != 64 || !d.Sha256.All(char.IsAsciiHexDigit) || d.RetainUntilUtc is DateTime date && (date.Kind != DateTimeKind.Utc || date == DateTime.MinValue || date == DateTime.MaxValue)) throw new MigrationRejectedException(); }
    }
    private static void Reference(string value) { if (!value.StartsWith("protected:", StringComparison.Ordinal) || value.Length is < 11 or > 512 || value.Any(char.IsControl)) throw new MigrationRejectedException(); }
}
[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, AllowDuplicateProperties = false)]
[JsonSerializable(typeof(KeyDependencyPlan))]
[JsonSerializable(typeof(KeyRetirementAssessment))]
public sealed partial class KeyDependencyJsonContext : JsonSerializerContext;
