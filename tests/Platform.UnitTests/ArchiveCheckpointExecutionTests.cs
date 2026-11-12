using Platform.Migration;
using Platform.SchemaRunner;
namespace Platform.UnitTests;

public sealed class ArchiveCheckpointExecutionTests
{
    [Fact]
    public async Task MatchingCheckpointStillExecutesVerifier()
    {
        int calls = 0; var saved = new ArchiveFamilyCheckpointResult(2, 3);
        var result = await ArchiveFamilyCheckpoint.VerifyAsync(saved, () => { calls++; return Task.FromResult(new ArchiveFamilyCheckpointResult(2, 3)); });
        Assert.Equal(1, calls); Assert.Equal(saved, result);
    }
    [Fact]
    public async Task AuthenticatedCallerCountsCannotReplaceComputedResults()
    {
        byte[] key = new byte[32], inputs = new byte[32]; byte[] encoded = ArchiveFamilyCheckpoint.Create(1, 2, 99, inputs, key); var saved = ArchiveFamilyCheckpoint.Open(encoded, 1, inputs, key); int calls = 0;
        await Assert.ThrowsAsync<MigrationRejectedException>(() => ArchiveFamilyCheckpoint.VerifyAsync(saved, () => { calls++; return Task.FromResult(new ArchiveFamilyCheckpointResult(2, 3)); })); Assert.Equal(1, calls);
    }
    [Fact]
    public async Task SavedCheckpointCannotHideVerifierFailure()
    {
        await Assert.ThrowsAsync<MigrationRejectedException>(() => ArchiveFamilyCheckpoint.VerifyAsync(new(2, 3), () => throw new MigrationRejectedException()));
        await Assert.ThrowsAsync<MigrationRejectedException>(() => ArchiveFamilyCheckpoint.VerifyAsync(null, () => Task.FromResult(new ArchiveFamilyCheckpointResult(-1, 0))));
        Assert.Equal(new ArchiveFamilyCheckpointResult(2, 3), await ArchiveFamilyCheckpoint.VerifyAsync(null, () => Task.FromResult(new ArchiveFamilyCheckpointResult(2, 3))));
    }
}
