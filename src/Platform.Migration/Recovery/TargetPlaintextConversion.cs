using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Platform.Persistence;
using Platform.Persistence.Migrations;
using Platform.Security;

namespace Platform.Migration.Recovery;

internal sealed record TargetConversionReceipt(string SourceSha256, string TargetSchemaSha256, string ModelSha256, string AssetManifestSha256, long Rows, int Assets, string? RuntimeReceiptSha256 = null);

internal sealed class TargetPlaintextConversion(SecretSnapshot keys, Func<Guid, BrandContext> contextFor)
{
    internal async Task<TargetConversionReceipt> RunAsync(SqliteConnection source, SqliteConnection destination,
        TargetConversionManifest manifest, ReadOnlyMemory<byte> schema, string assetIdentity,
        Func<TargetConversionAsset, CancellationToken, Task<byte[]>> readStored,
        Func<TargetConversionAsset, ReadOnlyMemory<byte>, byte[]> openAsset,
        Func<TargetConversionAsset, CancellationToken, Task<byte[]?>> readOutput,
        Func<TargetConversionAsset, ReadOnlyMemory<byte>, CancellationToken, Task>? createOutput,
        Action confirmCoverage,
        CancellationToken ct, string? stagedDestination = null,
        Action<TargetAssetReference, TargetConversionAsset>? visit = null)
    {
        if (schema.Length != 32 || string.IsNullOrWhiteSpace(assetIdentity)) throw new MigrationRejectedException();
        bool verifyOnly = createOutput is null;
        byte[] snapshot = Convert.FromHexString(manifest.Snapshot);
        await using var model = new PlainTargetDbContext(new DbContextOptionsBuilder<PlainTargetDbContext>().UseSqlite(destination).Options);
        string modelSha256 = Convert.ToHexStringLower(PlainTargetSchema.ModelDigest(model));
        if (stagedDestination is not null && (!verifyOnly || !Path.IsPathFullyQualified(stagedDestination)))
            throw new MigrationRejectedException();
        string destinationIdentity = stagedDestination ?? new SqliteConnectionStringBuilder(destination.ConnectionString).DataSource;
        byte[] binding = Bind(source, destinationIdentity, schema.Span, modelSha256, manifest.Commitment, assetIdentity);
        var copy = new SqlitePlaintextRowCopy(keys, contextFor);
        await manifest.CheckCoverageAsync(source, ct, visit);
        foreach (var asset in manifest.OrderedAssets())
        {
            byte[] plain = await OpenAsync(asset, readStored, openAsset, ct);
            CryptographicOperations.ZeroMemory(plain);
        }
        long rows = await copy.TransferAsync(source, destination, snapshot, verifyOnly, ct, binding, schema);
        foreach (var asset in manifest.OrderedAssets())
        {
            byte[] plain = await OpenAsync(asset, readStored, openAsset, ct);
            try
            {
                byte[]? stored = await readOutput(asset, ct);
                if (stored is null)
                {
                    if (createOutput is null) throw new MigrationRejectedException();
                    await createOutput(asset, plain, ct);
                    stored = await readOutput(asset, ct) ?? throw new MigrationRejectedException();
                }
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(stored, plain)) throw new MigrationRejectedException();
                }
                finally { CryptographicOperations.ZeroMemory(stored); }
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        if (!CryptographicOperations.FixedTimeEquals(await SqlitePlaintextRowCopy.InspectAsync(source, ct), snapshot))
            throw new MigrationRejectedException();
        confirmCoverage();
        await TargetConversionCheckpoint.CompleteAsync(destination, binding, manifest.AssetCount, verifyOnly, ct);
        return new(manifest.Snapshot, Convert.ToHexStringLower(schema.Span), modelSha256, manifest.Commitment, rows, manifest.AssetCount);
    }

    private static async Task<byte[]> OpenAsync(TargetConversionAsset asset,
        Func<TargetConversionAsset, CancellationToken, Task<byte[]>> readStored,
        Func<TargetConversionAsset, ReadOnlyMemory<byte>, byte[]> openAsset, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        byte[] stored = await readStored(asset, ct);
        try
        {
            if (Convert.ToHexStringLower(SHA256.HashData(stored)) != asset.StoredSha256) throw new MigrationRejectedException();
            byte[] plain = openAsset(asset, stored);
            if (ReferenceEquals(stored, plain)) throw new MigrationRejectedException();
            if (Convert.ToHexStringLower(SHA256.HashData(plain)) == asset.PlaintextSha256) return plain;
            CryptographicOperations.ZeroMemory(plain);
            throw new MigrationRejectedException();
        }
        finally { CryptographicOperations.ZeroMemory(stored); }
    }

    private byte[] Bind(SqliteConnection source, string destinationIdentity, ReadOnlySpan<byte> schema, string model, string manifest, string assetIdentity)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        SqliteStagingCopy.HashValue(hash, "akanti-sqlite-conversion-binding-v1");
        SqliteStagingCopy.HashValue(hash, new SqliteConnectionStringBuilder(source.ConnectionString).DataSource);
        SqliteStagingCopy.HashValue(hash, destinationIdentity);
        SqliteStagingCopy.HashValue(hash, Convert.ToHexStringLower(schema));
        SqliteStagingCopy.HashValue(hash, model);
        SqliteStagingCopy.HashValue(hash, manifest);
        SqliteStagingCopy.HashValue(hash, assetIdentity);
        byte[] recovery = keys.RecoveryCommitment();
        try { SqliteStagingCopy.HashValue(hash, recovery); return hash.GetHashAndReset(); }
        finally { CryptographicOperations.ZeroMemory(recovery); }
    }
}
