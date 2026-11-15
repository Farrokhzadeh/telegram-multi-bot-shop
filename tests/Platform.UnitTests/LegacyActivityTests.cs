using System.Security.Cryptography;
using Platform.Migration;
using Platform.Persistence;
using Platform.Testing;

namespace Platform.UnitTests;

public sealed class LegacyActivityTests
{
    [Theory]
    [InlineData("start", true, LegacyActivityException.UnverifiedInteraction)]
    [InlineData("shop", true, LegacyActivityException.UnverifiedInteraction)]
    [InlineData("product_view", true, LegacyActivityException.UnverifiedInteraction)]
    [InlineData("order_created", true, LegacyActivityException.UnverifiedCheckout)]
    [InlineData("deposit_approved", true, LegacyActivityException.UnsupportedProducer)]
    [InlineData("unknown", true, LegacyActivityException.UnsupportedProducer)]
    [InlineData("start", false, LegacyActivityException.UnboundMembership)]
    public async Task OriginalActivityIsPreservedWithoutInventingOperationalAuthority(string action, bool bound, LegacyActivityException reason)
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("Protected activity requires Linux.");
        using var keys = new SyntheticSecretFiles(); using var secrets = Platform.Security.SecretFiles.Load(keys.Root, "service-config.json", keys.Grant());
        var context = CipherQualification.Context(SyntheticSecretFiles.BrandA); Guid record = Guid.NewGuid(), member = Guid.NewGuid();
        string body = "{\"id\":7,\"actor_id\":999,\"user_id\":9,\"event_type\":\"" + action + "\",\"ref_type\":\"command\",\"ref_id\":\"RAW-CALLBACK\",\"details\":\"PRIVATE-DM-COUPON-RECEIPT-SECRET\",\"created_at\":\"2024-02-01T00:00:01+00:00\"}";
        Assert.Null(LegacySourceRecord.CopyCandidate("user_activity", "7", body));
        Assert.DoesNotContain(LegacyProjection.UserActivityRecord, LegacySourceRecord.Projections);
        var source = LegacySourceRecord.CopyCandidate("user_activity", "7", body, includeActivity: true)!;
        var proofs = new List<LegacyMapProof> { source.Proof(record, context, secrets) };
        if (bound) proofs.Add(new(LegacyProjection.User, "9", member, context.BrandId, SHA256.HashData("synthetic original user proof"u8)));
        byte[] bytes = VerifiedImportManifest.EncodePlan(SchemaManifest.ForQualification("Host=127.0.0.1;Database=shop_qualification_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa;Username=shop_test_runner"), "public-a", context.BrandId, "66371a23a1c8149dc5bdf89a0355aa689d454dba", SHA256.HashData("schema"u8), "protected:synthetic/activity", SHA256.HashData("snapshot"u8), Guid.NewGuid(), proofs);
        var manifest = VerifiedImportManifest.Open("protected:synthetic/activity", SHA256.HashData(bytes), bytes);
        string root = Path.Combine(Path.GetTempPath(), "teleg-history-archive-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var files = new LegacyArchiveFiles(root, secrets); var first = await files.WriteAsync(context, manifest, source, record, [], TestContext.Current.CancellationToken);
            var reopened = await new LegacyArchiveFiles(root, secrets).ReadAsync(context, manifest, record, TestContext.Current.CancellationToken);
            Assert.Equal(body, reopened.Source.Body); Assert.Equal(first.Digest, reopened.Digest);
            var review = new LegacyActivityReconciliation(context, secrets, manifest).Review(reopened);
            Assert.Equal(reason, review.Exception); Assert.Equal(bound ? member : (Guid?)null, review.Member);
            Assert.Equal(new DateTime(2024, 2, 1, 0, 0, 1, DateTimeKind.Utc), review.OccurredAt); Assert.False(review.OperationalEventImported);
            Assert.Equal("LegacyActivityDisposition [redacted]", review.ToString());
            Assert.DoesNotContain("PRIVATE-DM", File.ReadAllText(Path.Combine(root, record.ToString("N") + ".archive.json")));
            Assert.Throws<MigrationRejectedException>(() => new LegacyActivityReconciliation(CipherQualification.Context(SyntheticSecretFiles.BrandB), secrets, manifest));
            await Assert.ThrowsAsync<MigrationRejectedException>(() => files.WriteAsync(context, manifest, source with { Body = body.Replace("999", "998", StringComparison.Ordinal) }, record, [], TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, true); }
    }
}
