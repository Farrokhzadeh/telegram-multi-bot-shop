using Platform.Migration;
using Platform.SchemaRunner;
namespace Platform.UnitTests;

public sealed class ArchiveToolIdentityTests
{
    private static Dictionary<string, byte[]> Files() => new(StringComparer.Ordinal)
    {
        ["SchemaRunner.dll"] = Enumerable.Repeat((byte)1, 32).ToArray(),
        ["Platform.Migration.dll"] = Enumerable.Repeat((byte)2, 32).ToArray(),
        ["Platform.Security.dll"] = Enumerable.Repeat((byte)3, 32).ToArray(),
        ["Platform.Persistence.dll"] = Enumerable.Repeat((byte)4, 32).ToArray(),
        ["Npgsql.dll"] = Enumerable.Repeat((byte)5, 32).ToArray(),
        ["runner.deps.json"] = Enumerable.Repeat((byte)6, 32).ToArray(),
        ["runner.runtimeconfig.json"] = Enumerable.Repeat((byte)7, 32).ToArray(),
        ["dotnet"] = Enumerable.Repeat((byte)8, 32).ToArray(),
        ["libcrypto.so"] = Enumerable.Repeat((byte)9, 32).ToArray()
    };
    [Fact]
    public void AnonymousJitMemoryIsAllowedButDeletedDiskFilesAreRejected()
    {
        string[] maps = ["100-200 rw-s 00000000 00:01 123 /memfd:doublemapper (deleted)", "200-300 r-xp 00000000 00:02 124 /usr/lib/libsynthetic.so"];
        Assert.Equal(new[] { "/usr/lib/libsynthetic.so" }, ArchiveToolIdentity.MappedNativeFiles(maps).ToArray());
        Assert.Throws<MigrationRejectedException>(() => ArchiveToolIdentity.MappedNativeFiles(["200-300 r-xp 00000000 00:02 124 /usr/lib/libsynthetic.so (deleted)"]).ToArray());
        Assert.Throws<MigrationRejectedException>(() => ArchiveToolIdentity.MappedNativeFiles(["200-300 r-xp 00000000 00:02 124 /memfd:unknown (deleted)"]).ToArray());
    }
    [Fact]
    public void ManagedRuntimeAndEveryDependencyAffectBinding()
    {
        var files = Files(); var env = new Dictionary<string, string> { { "TZ", "UTC" } }; byte[] baseline = ArchiveToolIdentity.Bind("runtime-v1", files, env);
        foreach (string name in files.Keys)
        {
            var changed = Files(); changed[name][0] ^= 1; Assert.False(baseline.AsSpan().SequenceEqual(ArchiveToolIdentity.Bind("runtime-v1", changed, env)));
            var omitted = Files(); omitted.Remove(name); Assert.False(baseline.AsSpan().SequenceEqual(ArchiveToolIdentity.Bind("runtime-v1", omitted, env)));
        }
        Assert.False(baseline.AsSpan().SequenceEqual(ArchiveToolIdentity.Bind("runtime-v2", files, env)));
        Assert.False(baseline.AsSpan().SequenceEqual(ArchiveToolIdentity.Bind("runtime-v1", files, new Dictionary<string, string> { { "TZ", "changed" } })));
    }
    [Fact]
    public void DependencyOrderingIsCanonicalAndInvalidMetadataRejected()
    {
        var files = Files(); var reordered = files.Reverse().ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal); var env = new Dictionary<string, string>();
        Assert.Equal(ArchiveToolIdentity.Bind("runtime", files, env), ArchiveToolIdentity.Bind("runtime", reordered, env));
        Assert.Throws<MigrationRejectedException>(() => ArchiveToolIdentity.Bind("", files, env));
        Assert.Throws<MigrationRejectedException>(() => ArchiveToolIdentity.Bind("runtime", new Dictionary<string, byte[]>(), env));
        files["Npgsql.dll"] = new byte[31]; Assert.Throws<MigrationRejectedException>(() => ArchiveToolIdentity.Bind("runtime", files, env));
    }
}
