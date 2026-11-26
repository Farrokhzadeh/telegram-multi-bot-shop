using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Platform.Persistence;
using Platform.Persistence.Entities;

namespace Platform.Migration.Recovery;

internal sealed record TargetAssetReference(string Table, string Field, string RowCommitment, string Reference, bool DatabaseManifest, string? PayloadSha256, Guid? EvidencePayment = null)
{
    public override string ToString() => "Target asset reference (redacted)";
}

internal static class TargetAssetReferences
{
    internal static void Hash(IncrementalHash hash, TargetAssetReference entry)
    {
        SqliteStagingCopy.HashValue(hash, entry.Table);
        SqliteStagingCopy.HashValue(hash, entry.Field);
        SqliteStagingCopy.HashValue(hash, entry.RowCommitment);
        SqliteStagingCopy.HashValue(hash, entry.Reference);
        SqliteStagingCopy.HashValue(hash, entry.DatabaseManifest ? 1L : 0L);
        SqliteStagingCopy.HashValue(hash, entry.PayloadSha256);
    }

    internal static Action<object>? Inspect(IEntityType entity, Action<TargetAssetReference> visit)
    {
        if (entity.ClrType != typeof(Brand) && entity.ClrType != typeof(BrandProduct) &&
            entity.ClrType != typeof(Campaign) && entity.ClrType != typeof(LegacyIdMap) &&
            entity.ClrType != typeof(Order) && entity.ClrType != typeof(Payment) &&
            entity.ClrType != typeof(Product) && entity.ClrType != typeof(SupplierRequest) &&
            entity.ClrType != typeof(SupplierRequestAttempt) && entity.ClrType != typeof(SupportMessage)) return null;
        var table = entity.GetTableName() ?? throw new MigrationRejectedException();
        var key = entity.FindPrimaryKey()!.Properties.Select(property => property.PropertyInfo!).ToArray();
        return row =>
        {
            string? identity = null;
            void Add(string field, string? reference, bool databaseManifest = false, byte[]? digest = null)
            {
                if (reference is null) return;
                if (identity is null)
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    SqliteStagingCopy.HashValue(hash, table);
                    foreach (var property in key)
                        SqliteStagingCopy.HashValue(hash, SqliteStagingCopy.ConvertValue(property.GetValue(row)));
                    identity = Convert.ToHexStringLower(hash.GetHashAndReset());
                }
                Guid? payment = row is Payment value && field == nameof(Payment.EvidenceAssetRef) ? value.Id : null;
                visit(new(table, field, identity, reference, databaseManifest, digest is null ? null : Convert.ToHexStringLower(digest), payment));
            }

            switch (row)
            {
                case Brand value: Add(nameof(value.LogoAssetRef), value.LogoAssetRef); break;
                case BrandProduct value: Add(nameof(value.IconOverride), value.IconOverride); break;
                case Campaign value: Add(nameof(value.MediaRef), value.MediaRef); break;
                case LegacyIdMap value: Add(nameof(value.ImportManifestRef), value.ImportManifestRef); break;
                case Order value:
                    Add(nameof(value.ProductIconSnapshot), value.ProductIconSnapshot);
                    Add(nameof(value.ManifestAssetRef), value.ManifestAssetRef,
                        value.DeliveryGeneration is Guid generation && value.ManifestAssetRef == DeliveryManifestEncoding.Reference(value.Id, generation), value.ManifestDigest);
                    break;
                case Payment value:
                    Add(nameof(value.ReceiptAssetRef), value.ReceiptAssetRef);
                    Add(nameof(value.EvidenceAssetRef), value.EvidenceAssetRef, digest: value.EvidenceDigest);
                    break;
                case Product value: Add(nameof(value.IconRef), value.IconRef); break;
                case SupplierRequest value: Add(nameof(value.EvidenceRef), value.EvidenceRef); break;
                case SupplierRequestAttempt value: Add(nameof(value.EvidenceRef), value.EvidenceRef); break;
                case SupportMessage value: Add(nameof(value.AssetRef), value.AssetRef); break;
                default: throw new MigrationRejectedException();
            }
        };
    }
}
