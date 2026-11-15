using System.Globalization;
using System.Text.Json;
using Platform.Security;

namespace Platform.Migration;

public enum LegacyActivityException { UnboundMembership, UnverifiedInteraction, UnverifiedCheckout, UnsupportedProducer }
public sealed record LegacyActivityDisposition(Guid Record, Guid Brand, Guid? Member, DateTime OccurredAt, LegacyActivityException Exception)
{
    public bool OperationalEventImported => false;
    public override string ToString() => "LegacyActivityDisposition [redacted]";
}
public sealed class LegacyActivityReconciliation(BrandContext context, SecretSnapshot secrets, VerifiedImportManifest manifest)
{
    private readonly Dictionary<string, Guid> members = manifest.TargetBrand == context.BrandId ? manifest.Proofs.Where(p => p.Projection == LegacyProjection.User).ToDictionary(p => p.LegacyId, p => p.Target, StringComparer.Ordinal) : throw new MigrationRejectedException();
    public LegacyActivityDisposition Review(ArchivedLegacyRecord archived)
    {
        var source = archived.Source;
        if (source.Projection != LegacyProjection.UserActivityRecord || manifest.TargetBrand != context.BrandId || !new LegacyRecordArchive(secrets).Matches(context, archived, archived.Evidence)) throw new MigrationRejectedException();
        manifest.Require(source.Proof(archived.Target, context, secrets));
        using var document = source.Open(); var row = document.RootElement;
        string[] fields = ["id", "actor_id", "event_type", "ref_type", "ref_id", "details", "created_at", "user_id"];
        if (row.EnumerateObject().Count() != fields.Length || fields.Any(f => !row.TryGetProperty(f, out _)) || !row.GetProperty("id").TryGetInt64(out long id) || id <= 0 || id.ToString(CultureInfo.InvariantCulture) != source.Id ||
            !row.GetProperty("created_at").TryGetDateTimeOffset(out var occurred) || occurred.Offset != TimeSpan.Zero || occurred.UtcDateTime == DateTime.MinValue) throw new MigrationRejectedException();
        foreach (string field in new[] { "actor_id", "user_id" }) if (row.GetProperty(field).ValueKind != JsonValueKind.Null && !row.GetProperty(field).TryGetInt64(out _)) throw new MigrationRejectedException();
        foreach (string field in new[] { "event_type", "ref_type", "ref_id", "details" }) if (row.GetProperty(field).ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw new MigrationRejectedException();
        string? owner = row.GetProperty("user_id").ValueKind == JsonValueKind.Null ? null : row.GetProperty("user_id").GetInt64().ToString(CultureInfo.InvariantCulture);
        Guid? member = owner is null ? null : members.TryGetValue(owner, out Guid target) ? target : null;
        string? action = row.GetProperty("event_type").GetString();
        var reason = member is null ? LegacyActivityException.UnboundMembership : action switch
        {
            "start" or "shop" or "product_view" => LegacyActivityException.UnverifiedInteraction,
            "order_created" => LegacyActivityException.UnverifiedCheckout,
            _ => LegacyActivityException.UnsupportedProducer
        };
        return new(archived.Target, context.BrandId, member, occurred.UtcDateTime, reason);
    }
}
