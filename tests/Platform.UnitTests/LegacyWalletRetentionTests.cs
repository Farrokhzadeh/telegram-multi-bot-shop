using System.Security.Cryptography;
using System.Text;
using Platform.Migration;
using Platform.Persistence;
namespace Platform.UnitTests;

public sealed class LegacyWalletRetentionTests
{
    private const string Archive = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Body = """
        {"version":2,"policy":"manual_irr_and_original_nonposting_history_v2","sources":[{"source":"prod1","brand":"acc-abad","archive_sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","source_users":2,"wallets":[{"legacy_user_id":"101","original_irr_amount":"1234.5000"},{"legacy_user_id":"102","original_irr_amount":"0"}],"history":[{"id":"one","user_id":"101","amount":"-3.25","currency":"IRR","created_at":"2024-01-01 00:00:00+00"},{"id":"two","user_id":"102","amount":"0","currency":"USD","created_at":"2024-01-02T00:00:00Z"}]},{"source":"prod2","brand":"kuala"},{"source":"prod3","brand":"zikotech"}]}
        """;
    private static IReadOnlyDictionary<string, LegacyWalletRetention> Open(string body)
    { byte[] bytes = Encoding.UTF8.GetBytes(body); return LegacyWalletRetention.OpenAll("protected:synthetic/retention", SHA256.HashData(bytes), bytes, "acc-abad", Archive); }
    [Fact]
    public void SourceIndexKeepsSeparateOwnersNativeDenominationsZeroHistoryAndPrivateOutput()
    {
        var rows = Open(Body); Assert.Equal(2, rows.Count); Assert.True(rows["101"].ManualIrrBalance); Assert.False(rows["102"].ManualIrrBalance);
        Assert.Equal(1L, rows["101"].IrrHistoryRows); Assert.Equal(1L, rows["102"].ZeroHistoryRows); Assert.Equal(1L, rows["101"].HistoryRows);
        Assert.Equal("LegacyWalletRetention [redacted]", rows["101"].ToString()); Assert.DoesNotContain("1234", rows["101"].ToString(), StringComparison.Ordinal);
    }
    [Fact]
    public void StagingWalletRegisterRequiresThePinnedSnapshotAndItsOwnScope()
    {
        string staging = Body.Replace("\"source\":\"prod1\"", "\"source\":\"staging\"", StringComparison.Ordinal)
            .Replace(Archive, StagingWalletSnapshot.ArchiveSha256, StringComparison.Ordinal)
            .Replace(",{\"source\":\"prod2\",\"brand\":\"kuala\"},{\"source\":\"prod3\",\"brand\":\"zikotech\"}", "", StringComparison.Ordinal);
        byte[] bytes = Encoding.UTF8.GetBytes(staging), hash = SHA256.HashData(bytes);
        var wallets = LegacyWalletRetention.OpenAll(StagingWalletSnapshot.RetentionReference, hash, bytes, StagingWalletSnapshot.Brand, StagingWalletSnapshot.ArchiveSha256);
        Assert.Equal(2, wallets.Count);
        Assert.True(wallets["101"].ManualIrrBalance);
        Assert.Throws<MigrationRejectedException>(() => LegacyWalletRetention.OpenAll("protected:synthetic/retention", hash, bytes, StagingWalletSnapshot.Brand, StagingWalletSnapshot.ArchiveSha256));
        Assert.Throws<MigrationRejectedException>(() => LegacyWalletRetention.OpenAll(StagingWalletSnapshot.RetentionReference, hash, bytes, StagingWalletSnapshot.Brand, Archive));
    }
    [Fact]
    public void StagingHistoryRequiresTheExactSnapshotRevisionAndTargetBrand()
    {
        var target = SchemaManifest.ForQualification("Host=127.0.0.1;Database=shop_qualification_0123456789abcdef0123456789abcdef;Username=shop_test_runner");
        VerifiedImportManifest Build(string brand, Guid targetBrand, string revision, string snapshot)
        {
            var proof = new LegacyMapProof(LegacyProjection.User, "101", Guid.NewGuid(), targetBrand, new byte[32]);
            byte[] bytes = VerifiedImportManifest.EncodePlan(target, brand, targetBrand, revision, new byte[32],
                "protected:synthetic/staging-snapshot", Convert.FromHexString(snapshot), Guid.NewGuid(), [proof]);
            return VerifiedImportManifest.Open("protected:synthetic/staging-manifest", SHA256.HashData(bytes), bytes);
        }

        var brand = StagingWalletSnapshot.Brand;
        var targetBrand = StagingWalletSnapshot.TargetBrand;
        var revision = StagingWalletSnapshot.LegacyRevision;
        var snapshot = StagingWalletSnapshot.ArchiveSha256;
        Assert.True(StagingWalletSnapshot.Matches(Build(brand, targetBrand, revision, snapshot)));
        Assert.False(StagingWalletSnapshot.Matches(Build("kuala", targetBrand, revision, snapshot)));
        Assert.False(StagingWalletSnapshot.Matches(Build(brand, Guid.NewGuid(), revision, snapshot)));
        Assert.False(StagingWalletSnapshot.Matches(Build(brand, targetBrand, new string('a', 40), snapshot)));
        Assert.False(StagingWalletSnapshot.Matches(Build(brand, targetBrand, revision, Archive)));
    }

    [Theory]
    [InlineData("\"version\":2", "\"version\":1,\"version\":2")]
    [InlineData("\"source_users\":2", "\"source_users\":3")]
    [InlineData("\"legacy_user_id\":\"102\"", "\"legacy_user_id\":\"101\"")]
    [InlineData("\"legacy_user_id\":\"102\"", "\"legacy_user_id\":\"0102\"")]
    [InlineData("\"user_id\":\"102\"", "\"user_id\":\"103\"")]
    [InlineData("\"id\":\"two\"", "\"id\":\"one\"")]
    [InlineData("\"amount\":\"0\"", "\"amount\":\"NaN\"")]
    [InlineData("\"currency\":\"USD\"", "\"currency\":\"EUR\"")]
    [InlineData("2024-01-02T00:00:00Z", "2024-01-02T00:00:00")]
    [InlineData("2024-01-02T00:00:00Z", "2024-01-02T00:00:00+01:00")]
    [InlineData("\"brand\":\"kuala\"", "\"brand\":\"zikotech\"")]
    public void AmbiguousIncompleteOrChangedFactsRejectForEveryOwner(string before, string after) => Assert.Throws<MigrationRejectedException>(() => Open(Body.Replace(before, after, StringComparison.Ordinal)));
    [Fact]
    public void ChangedBytesWrongArchiveMissingOwnerAndWrongDigestReject()
    {
        byte[] body = Encoding.UTF8.GetBytes(Body), hash = SHA256.HashData(body);
        Assert.Throws<MigrationRejectedException>(() => LegacyWalletRetention.Open("protected:synthetic/retention", hash, body, "acc-abad", Archive, "103"));
        Assert.Throws<MigrationRejectedException>(() => LegacyWalletRetention.OpenAll("protected:synthetic/retention", hash, body, "acc-abad", new string('b', 64)));
        body[^2] ^= 1;
        Assert.Throws<MigrationRejectedException>(() => LegacyWalletRetention.OpenAll("protected:synthetic/retention", hash, body, "acc-abad", Archive));
    }
}
