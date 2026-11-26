using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Platform.Persistence;

namespace Platform.Migration.Recovery;

internal enum TargetAssetFormat { Plain, PaymentEvidence, CustomerHistory, PreviewCapture, PreviewCursor }

internal sealed record TargetConversionAsset(string ReferenceCommitment, string StoredSha256, string PlaintextSha256,
    TargetAssetFormat Format = TargetAssetFormat.Plain, Guid Brand = default, Guid Row = default, string? KeyId = null, long UpdateId = 0)
{
    internal bool Detached => Format is TargetAssetFormat.CustomerHistory or TargetAssetFormat.PreviewCapture or TargetAssetFormat.PreviewCursor;
    public override string ToString() => "Target conversion asset (redacted)";
}

internal sealed class TargetConversionManifest
{
    private readonly Dictionary<string, TargetConversionAsset> assets = new(StringComparer.Ordinal);
    internal string Snapshot { get; }
    internal string Commitment { get; }
    internal int AssetCount => assets.Count;

    internal TargetConversionManifest(string snapshot, IEnumerable<TargetConversionAsset> entries)
    {
        RequireSha256(snapshot);
        Snapshot = snapshot;
        foreach (var entry in entries)
        {
            RequireSha256(entry.ReferenceCommitment);
            RequireSha256(entry.StoredSha256);
            RequireSha256(entry.PlaintextSha256);
            bool valid = entry.Format switch
            {
                TargetAssetFormat.Plain => entry.Brand == Guid.Empty && entry.Row == Guid.Empty && entry.KeyId is null && entry.UpdateId == 0 &&
                    entry.StoredSha256 == entry.PlaintextSha256,
                TargetAssetFormat.PaymentEvidence => entry.Brand != Guid.Empty && entry.Row != Guid.Empty && entry.KeyId is not null && entry.UpdateId == 0,
                TargetAssetFormat.CustomerHistory => entry.Brand != Guid.Empty && entry.Row != Guid.Empty && entry.KeyId is null && entry.UpdateId == 0,
                TargetAssetFormat.PreviewCapture => entry.Brand == Guid.Empty && entry.Row == Guid.Empty && entry.KeyId is null && entry.UpdateId is > 0 and < long.MaxValue,
                TargetAssetFormat.PreviewCursor => entry.Brand == Guid.Empty && entry.Row == Guid.Empty && entry.KeyId is null &&
                    entry.UpdateId is >= 0 and <= int.MaxValue && entry.StoredSha256 == entry.PlaintextSha256,
                _ => false
            };
            if (!valid || entry.Detached && entry.ReferenceCommitment != DetachedCommitment(entry.Format, entry.Brand, entry.Row, entry.UpdateId) ||
                assets.Count >= 100_000 || !assets.TryAdd(entry.ReferenceCommitment, entry))
                throw new MigrationRejectedException();
        }
        var cursors = assets.Values.Where(asset => asset.Format == TargetAssetFormat.PreviewCursor).Take(2).ToArray();
        var captures = assets.Values.Where(asset => asset.Format == TargetAssetFormat.PreviewCapture).ToArray();
        if (cursors.Length > 1 || captures.Length > 0 && (cursors.Length != 1 || captures.Any(asset => asset.UpdateId > cursors[0].UpdateId)))
            throw new MigrationRejectedException();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        SqliteStagingCopy.HashValue(hash, "akanti-sqlite-conversion-assets-v1");
        SqliteStagingCopy.HashValue(hash, snapshot);
        foreach (var entry in OrderedAssets())
        {
            SqliteStagingCopy.HashValue(hash, entry.ReferenceCommitment);
            SqliteStagingCopy.HashValue(hash, entry.StoredSha256);
            SqliteStagingCopy.HashValue(hash, entry.PlaintextSha256);
            SqliteStagingCopy.HashValue(hash, (long)entry.Format);
            SqliteStagingCopy.HashValue(hash, entry.Brand.ToString("D"));
            SqliteStagingCopy.HashValue(hash, entry.Row.ToString("D"));
            SqliteStagingCopy.HashValue(hash, entry.KeyId);
            SqliteStagingCopy.HashValue(hash, entry.UpdateId);
        }
        Commitment = Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal async Task CheckCoverageAsync(SqliteConnection source, CancellationToken ct, Action<TargetAssetReference, TargetConversionAsset>? visit = null)
    {
        var remaining = assets.Values.Where(asset => !asset.Detached).Select(asset => asset.ReferenceCommitment).ToHashSet(StringComparer.Ordinal);
        byte[] result = await SqlitePlaintextRowCopy.InspectAsync(source, ct, reference =>
        {
            if (reference.DatabaseManifest) return;
            string commitment = ReferenceCommitment(reference);
            if (!remaining.Remove(commitment)) throw new MigrationRejectedException();
            var asset = assets[commitment];
            if (reference.PayloadSha256 is string sha256 && asset.PlaintextSha256 != sha256 ||
                asset.Format == TargetAssetFormat.PaymentEvidence && reference.EvidencePayment != asset.Row)
                throw new MigrationRejectedException();
            visit?.Invoke(reference, asset);
        });
        if (remaining.Count != 0 || Convert.ToHexStringLower(result) != Snapshot)
            throw new MigrationRejectedException();
    }

    internal IOrderedEnumerable<TargetConversionAsset> OrderedAssets() => assets.Values.OrderBy(entry => entry.ReferenceCommitment, StringComparer.Ordinal);

    internal static string ReferenceCommitment(TargetAssetReference reference)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        SqliteStagingCopy.HashValue(hash, "akanti-sqlite-asset-reference-v1");
        TargetAssetReferences.Hash(hash, reference);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal static string DetachedCommitment(TargetAssetFormat format, Guid brand, Guid row, long update)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        SqliteStagingCopy.HashValue(hash, "akanti-sqlite-detached-asset-v1");
        SqliteStagingCopy.HashValue(hash, (long)format);
        SqliteStagingCopy.HashValue(hash, brand.ToString("D"));
        SqliteStagingCopy.HashValue(hash, row.ToString("D"));
        SqliteStagingCopy.HashValue(hash, update);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void RequireSha256(string value)
    {
        if (value.Length != 64 || value.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new MigrationRejectedException();
    }
}
