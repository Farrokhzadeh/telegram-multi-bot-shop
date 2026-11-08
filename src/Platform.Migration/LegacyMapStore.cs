using System.Security.Cryptography;
using Platform.Persistence;

namespace Platform.Migration;

public static class LegacyMapStore
{
    public static async Task RequireDestinationAsync(DbSession session, VerifiedImportManifest manifest, CancellationToken ct = default)
    {
        manifest.Destination.Validate();
        if (manifest.Destination.SqlSha256 != SchemaRunner.BaselineSqlSha256) throw new MigrationRejectedException();
        var destination = manifest.Destination;
        if (!session.MatchesEndpoint(destination.Host, destination.Port, destination.Database, destination.Username)) throw new MigrationRejectedException();
        bool valid = await session.QuerySingleAsync(new DbStatement("SELECT current_database()=$1 AND current_user=$2 AND current_setting('server_version_num')::integer=$3 AND EXISTS(SELECT 1 FROM pg_roles WHERE rolname='shop_legacy_import' AND pg_has_role(current_user,oid,'MEMBER'))",
            DbValue.Text(manifest.Destination.Database), DbValue.Text(manifest.Destination.Username), DbValue.Integer(manifest.Destination.ServerVersion)), r => r.GetBoolean(0), ct);
        if (!valid) throw new MigrationRejectedException();
    }

    internal sealed record Coverage(long Committed, long Missing, long Mismatched, long Unexpected, long BatchCount);
    internal static async Task<Coverage> ReadCoverageAsync(DbSession session, VerifiedImportManifest manifest, CancellationToken ct = default)
    {
        await RequireDestinationAsync(session, manifest, ct);
        var expected = manifest.Proofs.ToDictionary(p => (p.SourceTable, p.KeyKind, p.LegacyId)); var seen = new HashSet<(string, string, string)>();
        (string Table, string Kind, string Id)? cursor = null;
        long committed = 0, mismatched = 0, inBatch = 0;
        while (true)
        {
            var rows = await session.QueryManyAsync(new DbStatement("""
                SELECT source_table,source_key_kind,legacy_id,target_kind,target_id,target_brand_id,source_fingerprint,import_batch,import_manifest_ref
                FROM shop.legacy_id_map WHERE source_brand=$1 AND ($2='' OR (source_table,source_key_kind,legacy_id)>($2,$3,$4))
                ORDER BY source_table,source_key_kind,legacy_id LIMIT 1000
                """, DbValue.Text(manifest.SourceBrand), DbValue.Text(cursor?.Table ?? ""), DbValue.Text(cursor?.Kind ?? ""), DbValue.Text(cursor?.Id ?? "")),
                r => (Table: r.GetString(0), Kind: r.GetString(1), Id: r.GetString(2), TargetKind: r.GetString(3), Target: r.GetGuid(4),
                    Brand: r.IsDBNull(5) ? Guid.Empty : r.GetGuid(5), Digest: r.GetBinary(6), Batch: r.GetGuid(7), Reference: r.GetString(8)), 1000, ct);
            foreach (var row in rows)
            {
                var key = (row.Table, row.Kind, row.Id); cursor = key;
                if (row.Batch == manifest.Batch && row.Reference == manifest.Reference) inBatch++;
                if (!expected.TryGetValue(key, out var proof)) continue;
                if (!seen.Add(key)) throw new MigrationRejectedException();
                committed++;
                if (row.TargetKind != proof.TargetKind || row.Target != proof.Target || row.Brand != proof.Brand || row.Batch != manifest.Batch || row.Reference != manifest.Reference ||
                    !CryptographicOperations.FixedTimeEquals(row.Digest, proof.Fingerprint.Span)) mismatched++;
            }
            if (rows.Count < 1000) break;
        }
        long batchCount = await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.legacy_id_map WHERE import_batch=$1 AND import_manifest_ref=$2",
            DbValue.Uuid(manifest.Batch), DbValue.Text(manifest.Reference)), r => r.GetInt64(0), ct);
        if (batchCount < inBatch) throw new MigrationRejectedException();
        return new(committed, expected.Count - committed, mismatched, batchCount - (committed - mismatched), batchCount);
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
