using System.Security.Cryptography;
using System.Text.Json;
using Platform.Migration;
using Platform.Persistence;

namespace Platform.UnitTests;

public sealed class LegacyOriginalReviewQualificationTests
{
    private static (VerifiedImportManifest Manifest, LegacyMapProof[] Proofs) Fixture()
    {
        Guid brand = Guid.NewGuid();
        var proofs = Enumerable.Range(0, 3).Select(i => new LegacyMapProof(LegacyProjection.OrderRecord,
            "ORD-" + i, Guid.NewGuid(), brand, SHA256.HashData(BitConverter.GetBytes(i)))).ToArray();
        byte[] plan = VerifiedImportManifest.EncodePlan(
            SchemaManifest.ForQualification("Host=127.0.0.1;Database=shop_qualification_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa;Username=shop_test_runner"),
            "acc-abad", brand, "66371a23a1c8149dc5bdf89a0355aa689d454dba",
            SHA256.HashData("schema"u8), "protected:synthetic/order-review", SHA256.HashData("source"u8),
            Guid.NewGuid(), proofs);
        return (VerifiedImportManifest.Open("protected:synthetic/order-review", SHA256.HashData(plan), plan), proofs);
    }
    private static string Review(VerifiedImportManifest manifest, LegacyMapProof[] proofs, bool missing = false,
        bool wrongTarget = false, bool duplicate = false, bool changedHash = false, string? reason = null)
    {
        var entries = proofs.Take(missing ? 1 : 2).Select(p => new
        {
            projection = p.Projection.ToString(),
            legacy_id = p.LegacyId,
            target = (wrongTarget && p == proofs[0] ? Guid.NewGuid() : p.Target).ToString(),
            reason = reason ?? "incomplete_order_terms"
        }).ToList();
        if (duplicate) entries.Add(entries[0]);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("archive_manifest_sha256", changedHash ? new string('0', 64) : Convert.ToHexStringLower(manifest.Digest.Span));
            writer.WriteString("typed_manifest_sha256", new string('a', 64));
            writer.WriteNumber("original_records", 3);
            writer.WriteNumber("typed_orders", 1);
            writer.WriteNumber("typed_payments", 0);
            writer.WriteNumber("native_zero_proofs", 0);
            writer.WriteNumber("inactive_master_catalog", 0);
            writer.WriteNumber("reviewed_records", entries.Count);
            writer.WriteStartObject("review_reason_counts");
            writer.WriteNumber(reason ?? "incomplete_order_terms", entries.Count);
            writer.WriteEndObject();
            writer.WriteStartArray("review");
            foreach (var entry in entries)
            {
                writer.WriteStartObject();
                writer.WriteString("projection", entry.projection);
                writer.WriteString("legacy_id", entry.legacy_id);
                writer.WriteString("target", entry.target);
                writer.WriteString("reason", entry.reason);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
    [Fact]
    public void OriginalReviewCasesAreIndividuallyBoundToSourceProofs()
    {
        var (manifest, proofs) = Fixture();
        var result = LegacyOriginalReviewQualification.Verify(manifest, Review(manifest, proofs), new string('a', 64), 1);
        Assert.Equal(3, result.OriginalRecords);
        Assert.Equal(2, result.ReviewedRecords);
        Assert.Equal(2, result.ReviewedOrders);
        Assert.Equal(1, result.TypedOrders);
        Assert.Equal("LegacyOriginalReviewAudit [redacted]", result.ToString());
    }
    [Fact]
    public void IncompleteOrContradictoryReviewCasesAreRejected()
    {
        var (manifest, proofs) = Fixture();
        Assert.Throws<MigrationRejectedException>(() => LegacyOriginalReviewQualification.Verify(manifest, Review(manifest, proofs, missing: true), new string('a', 64), 1));
        Assert.Throws<MigrationRejectedException>(() => LegacyOriginalReviewQualification.Verify(manifest, Review(manifest, proofs, wrongTarget: true), new string('a', 64), 1));
        Assert.Throws<MigrationRejectedException>(() => LegacyOriginalReviewQualification.Verify(manifest, Review(manifest, proofs, duplicate: true), new string('a', 64), 1));
        Assert.Throws<MigrationRejectedException>(() => LegacyOriginalReviewQualification.Verify(manifest, Review(manifest, proofs, changedHash: true), new string('a', 64), 1));
        Assert.Throws<MigrationRejectedException>(() => LegacyOriginalReviewQualification.Verify(manifest, Review(manifest, proofs, reason: "owner_approved"), new string('a', 64), 1));
        Assert.Throws<MigrationRejectedException>(() => LegacyOriginalReviewQualification.Verify(manifest, Review(manifest, proofs), new string('b', 64), 1));
        Assert.Throws<MigrationRejectedException>(() => LegacyOriginalReviewQualification.Verify(manifest, Review(manifest, proofs), new string('a', 64), 2));
    }
}
