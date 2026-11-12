using Platform.Migration;
using Platform.SchemaRunner;
namespace Platform.UnitTests;

public sealed class ArchiveFamilyCheckpointTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(n => (byte)n).ToArray();
    private static readonly byte[] Input = Enumerable.Range(31, 32).Select(n => (byte)n).ToArray();
    [Fact]
    public void RestartAcceptsSameProjectionAndExactBoundInputs()
    {
        byte[] saved = ArchiveFamilyCheckpoint.Create(19, 4, 2, Input, Key); var resumed = ArchiveFamilyCheckpoint.Open(saved, 19, Input, Key);
        Assert.Equal(4, resumed.Count); Assert.Equal(2, resumed.RecoveredFields);
    }
    [Fact]
    public void ChangedInputProjectionKeyOrReceiptCannotBeReused()
    {
        byte[] saved = ArchiveFamilyCheckpoint.Create(19, 4, 2, Input, Key), input = (byte[])Input.Clone(), key = (byte[])Key.Clone(), body = (byte[])saved.Clone(), tag = (byte[])saved.Clone(); input[0] ^= 1; key[0] ^= 1; body[2] ^= 1; tag[^1] ^= 1;
        Assert.Throws<MigrationRejectedException>(() => ArchiveFamilyCheckpoint.Open(saved, 20, Input, Key));
        Assert.Throws<MigrationRejectedException>(() => ArchiveFamilyCheckpoint.Open(saved, 19, input, Key));
        Assert.Throws<MigrationRejectedException>(() => ArchiveFamilyCheckpoint.Open(saved, 19, Input, key));
        Assert.Throws<MigrationRejectedException>(() => ArchiveFamilyCheckpoint.Open(body, 19, Input, Key));
        Assert.Throws<MigrationRejectedException>(() => ArchiveFamilyCheckpoint.Open(tag, 19, Input, Key));
        Assert.Throws<MigrationRejectedException>(() => ArchiveFamilyCheckpoint.Open(saved[..^1], 19, Input, Key));
    }
    [Fact]
    public void MalformedOrNegativeInputsAreRejected()
    {
        Assert.Throws<MigrationRejectedException>(() => ArchiveFamilyCheckpoint.Create(-1, 0, 0, Input, Key));
        Assert.Throws<MigrationRejectedException>(() => ArchiveFamilyCheckpoint.Create(19, -1, 0, Input, Key));
        Assert.Throws<MigrationRejectedException>(() => ArchiveFamilyCheckpoint.Create(19, 0, -1, Input, Key));
        Assert.Throws<MigrationRejectedException>(() => ArchiveFamilyCheckpoint.Create(19, 0, 0, [], Key));
        Assert.Throws<MigrationRejectedException>(() => ArchiveFamilyCheckpoint.Create(19, 0, 0, Input, []));
    }
}
