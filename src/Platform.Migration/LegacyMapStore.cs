using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Entities;
using Platform.Persistence;

namespace Platform.Migration;

public static class LegacyMapStore
{
    public static async Task RequireDestinationAsync(DbSession session, VerifiedImportManifest manifest, CancellationToken ct = default)
    {
        session.RequireOfflineImport();
        manifest.Destination.Validate();
        if (manifest.Destination.SqlSha256 != SchemaRunner.BaselineSqlSha256) throw new MigrationRejectedException();
        var destination = manifest.Destination;
        if (!session.MatchesEndpoint(destination.Host, destination.Port, destination.Database, destination.Username)) throw new MigrationRejectedException();
        bool valid = await session.QuerySingleAsync(new DbStatement("SELECT current_database()=$1 AND current_user=$2 AND current_setting('server_version_num')::integer=$3 AND EXISTS(SELECT 1 FROM pg_roles WHERE rolname='shop_legacy_import' AND pg_has_role(current_user,oid,'MEMBER'))",
            DbValue.Text(manifest.Destination.Database), DbValue.Text(manifest.Destination.Username), DbValue.Integer(manifest.Destination.ServerVersion)), r => r.GetBoolean(0), ct);
        if (!valid) throw new MigrationRejectedException();
    }

    internal sealed record Coverage(long Committed, long Missing, long Mismatched, long Unexpected, long BatchCount);
    internal static async Task<Coverage> ReadCoverageAsync(DbSession session, VerifiedImportManifest manifest, CancellationToken ct = default, ReadOnlyReconciliationTarget? target = null)
    {
        if (target is null) await RequireDestinationAsync(session, manifest, ct); else await target.RequireAsync(session, manifest, ct);
        var expected = manifest.Proofs.ToDictionary(p => (p.SourceTable, p.KeyKind, p.LegacyId)); var seen = new HashSet<(string, string, string)>();
        (string Table, string Kind, string Id)? cursor = null;
        long committed = 0, mismatched = 0, inBatch = 0;
        while (true)
        {
            string table = cursor?.Table ?? "", kind = cursor?.Kind ?? "", legacy = cursor?.Id ?? "";
            var rows = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<LegacyIdMap>().Where(row => row.SourceBrand == manifest.SourceBrand && (table == "" || row.SourceTable.CompareTo(table) > 0 || row.SourceTable == table && (row.SourceKeyKind.CompareTo(kind) > 0 || row.SourceKeyKind == kind && row.LegacyId.CompareTo(legacy) > 0))).OrderBy(row => row.SourceTable).ThenBy(row => row.SourceKeyKind).ThenBy(row => row.LegacyId).Select(row => new { Table = row.SourceTable, Kind = row.SourceKeyKind, Id = row.LegacyId, row.TargetKind, Target = row.TargetId, Brand = row.TargetBrandId ?? Guid.Empty, Digest = row.SourceFingerprint, Batch = row.ImportBatch, Reference = row.ImportManifestRef }).Take(1000).ToListAsync(token), ct);
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
        long batchCount = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<LegacyIdMap>().LongCountAsync(row => row.ImportBatch == manifest.Batch && row.ImportManifestRef == manifest.Reference, token), ct);
        if (batchCount < inBatch) throw new MigrationRejectedException();
        return new(committed, expected.Count - committed, mismatched, batchCount - (committed - mismatched), batchCount);
    }
    public static async Task<bool> EnsureAsync(DbSession session, VerifiedImportManifest manifest, LegacyMapProof proof, CancellationToken ct = default)
    {
        manifest.Require(proof);
        await RequireDestinationAsync(session, manifest, ct);
        byte[] identity = System.Text.Encoding.UTF8.GetBytes("legacy-map-v1:" + manifest.SourceBrand + ":" + proof.Key);
        await session.ExecuteAsync(new DbStatement("SELECT pg_advisory_xact_lock($1)", DbValue.Bigint(System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(identity)))), ct);
        var old = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<LegacyIdMap>().Where(row => row.SourceBrand == manifest.SourceBrand && row.SourceTable == proof.SourceTable && row.SourceKeyKind == proof.KeyKind && row.LegacyId == proof.LegacyId).Select(row => new { Kind = row.TargetKind, Id = row.TargetId, Brand = row.TargetBrandId ?? Guid.Empty, Digest = row.SourceFingerprint, Batch = row.ImportBatch, Reference = row.ImportManifestRef }).Take(1).ToListAsync(token), ct);
        if (old.Count != 0)
        {
            var value = old[0];
            if (value.Kind != proof.TargetKind || value.Id != proof.Target || value.Brand != proof.Brand || value.Batch != manifest.Batch || value.Reference != manifest.Reference || !CryptographicOperations.FixedTimeEquals(value.Digest, proof.Fingerprint.Span)) throw new MigrationRejectedException();
            return false;
        }
        byte[] fingerprint = proof.Fingerprint.ToArray();
        await session.RunEfStepAsync(true, (data, token) => { data.RequireImportCommand(); data.Data.Add(new LegacyIdMap { SourceBrand = manifest.SourceBrand, SourceTable = proof.SourceTable, SourceKeyKind = proof.KeyKind, LegacyId = proof.LegacyId, TargetKind = proof.TargetKind, TargetId = proof.Target, TargetBrandId = proof.Brand, SourceFingerprint = fingerprint, ImportBatch = manifest.Batch, ImportManifestRef = manifest.Reference }); return Task.FromResult(true); }, ct);
        return true;
    }
}
