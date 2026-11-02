using System.Security.Cryptography;
using Platform.Persistence;

namespace Platform.Migration;

public static class LegacyMapStore
{
    public static async Task RequireDestinationAsync(DbSession session, VerifiedImportManifest manifest, CancellationToken ct = default)
    {
        manifest.Destination.Validate();
        var destination = manifest.Destination;
        if (!session.MatchesEndpoint(destination.Host, destination.Port, destination.Database, destination.Username)) throw new MigrationRejectedException();
        bool valid = await session.QuerySingleAsync(new DbStatement("SELECT current_database()=$1 AND current_user=$2 AND current_setting('server_version_num')::integer=$3 AND EXISTS(SELECT 1 FROM pg_roles WHERE rolname='shop_legacy_import' AND pg_has_role(current_user,oid,'MEMBER'))",
            DbValue.Text(manifest.Destination.Database), DbValue.Text(manifest.Destination.Username), DbValue.Integer(manifest.Destination.ServerVersion)), r => r.GetBoolean(0), ct);
        if (!valid) throw new MigrationRejectedException();
    }
    public static async Task<bool> EnsureAsync(DbSession session, VerifiedImportManifest manifest, LegacyMapProof proof, CancellationToken ct = default)
    {
        manifest.Require(proof);
        await RequireDestinationAsync(session, manifest, ct);
        byte[] identity = System.Text.Encoding.UTF8.GetBytes("legacy-map-v1:" + manifest.SourceBrand + ":" + proof.Key);
        await session.ExecuteAsync(new DbStatement("SELECT pg_advisory_xact_lock($1)", DbValue.Bigint(System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(identity)))), ct);
        var old = await session.QueryManyAsync(new DbStatement("SELECT target_kind,target_id,target_brand_id,source_fingerprint,import_batch,import_manifest_ref FROM shop.legacy_id_map WHERE source_brand=$1 AND source_table=$2 AND source_key_kind=$3 AND legacy_id=$4",
            DbValue.Text(manifest.SourceBrand), DbValue.Text(proof.SourceTable), DbValue.Text(proof.KeyKind), DbValue.Text(proof.LegacyId)), r => (Kind: r.GetString(0), Id: r.GetGuid(1), Brand: r.IsDBNull(2) ? Guid.Empty : r.GetGuid(2), Digest: r.GetBinary(3), Batch: r.GetGuid(4), Reference: r.GetString(5)), 1, ct);
        if (old.Count != 0)
        {
            var value = old[0];
            if (value.Kind != proof.TargetKind || value.Id != proof.Target || value.Brand != proof.Brand || value.Batch != manifest.Batch || value.Reference != manifest.Reference || !CryptographicOperations.FixedTimeEquals(value.Digest, proof.Fingerprint.Span)) throw new MigrationRejectedException();
            return false;
        }
        await session.ExecuteAsync(new DbStatement("INSERT INTO shop.legacy_id_map(source_brand,source_table,source_key_kind,legacy_id,target_kind,target_id,target_brand_id,source_fingerprint,import_batch,import_manifest_ref) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)",
            DbValue.Text(manifest.SourceBrand), DbValue.Text(proof.SourceTable), DbValue.Text(proof.KeyKind), DbValue.Text(proof.LegacyId), DbValue.Text(proof.TargetKind), DbValue.Uuid(proof.Target), DbValue.Uuid(proof.Brand), DbValue.Binary(proof.Fingerprint.Span), DbValue.Uuid(manifest.Batch), DbValue.Text(manifest.Reference)), ct);
        return true;
    }
}
