using Platform.Migration;
using Platform.SchemaRunner;
namespace Platform.UnitTests;

public sealed class ArchiveFamilyBindingTests
{
    [Fact]
    public void ChangingAnyFrozenInputChangesCheckpointBinding()
    {
        var endpoint = new LegacySourceEndpoint("/root/teleg-source-qualification-test/socket", 5432, "teleg_source_prod1", "teleg_source_reader"); var brand = Guid.NewGuid(); byte[] archive = [1], index = [2], tool = [3]; string[] keys = ["synthetic-key"];
        byte[] Get(string source, byte[] plan, byte[] idx, byte[] version, LegacySourceEndpoint db, string sourceBrand, Guid identity, string keyId, string[] oldKeys) => ReadOnlySourceArchiveVerification.CommonBinding(source, plan, idx, version, db, sourceBrand, identity, keyId, oldKeys);
        byte[] valid = Get("source", archive, index, tool, endpoint, "acc-abad", brand, "current", keys);
        byte[][] changed = [Get("changed", archive, index, tool, endpoint, "acc-abad", brand, "current", keys), Get("source", [4], index, tool, endpoint, "acc-abad", brand, "current", keys), Get("source", archive, [4], tool, endpoint, "acc-abad", brand, "current", keys), Get("source", archive, index, [4], endpoint, "acc-abad", brand, "current", keys), Get("source", archive, index, tool, endpoint with { Database = "teleg_source_prod2" }, "acc-abad", brand, "current", keys), Get("source", archive, index, tool, endpoint, "zikotech", brand, "current", keys), Get("source", archive, index, tool, endpoint, "acc-abad", Guid.NewGuid(), "current", keys), Get("source", archive, index, tool, endpoint, "acc-abad", brand, "rotated", keys), Get("source", archive, index, tool, endpoint, "acc-abad", brand, "current", ["changed"])];
        Assert.All(changed, value => Assert.False(valid.AsSpan().SequenceEqual(value)));
    }
    [Fact]
    public async Task ChangedOrMissingArchiveFileCannotReuseFileSetBinding()
    {
        if (!OperatingSystem.IsLinux()) return;
        string directory = Path.Combine(Path.GetTempPath(), "teleg-history-archive-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            Guid target = Guid.NewGuid(); var proof = new LegacyMapProof(LegacyProjection.ProductRecord, "synthetic", target, Guid.NewGuid(), new byte[32]); string path = Path.Combine(directory, target.ToString("N") + ".archive.json");
            await File.WriteAllBytesAsync(path, new byte[128], TestContext.Current.CancellationToken); File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            byte[] before = await ReadOnlySourceArchiveVerification.FamilyHashAsync(directory, [proof], TestContext.Current.CancellationToken);
            byte[] changed = new byte[128]; changed[^1] = 1; await File.WriteAllBytesAsync(path, changed, TestContext.Current.CancellationToken);
            byte[] after = await ReadOnlySourceArchiveVerification.FamilyHashAsync(directory, [proof], TestContext.Current.CancellationToken); Assert.False(before.AsSpan().SequenceEqual(after));
            File.Delete(path); await Assert.ThrowsAsync<MigrationRejectedException>(() => ReadOnlySourceArchiveVerification.FamilyHashAsync(directory, [proof], TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(directory, true); }
    }
}
