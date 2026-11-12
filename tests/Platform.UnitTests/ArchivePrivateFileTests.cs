using System.Diagnostics;
using Platform.Migration;
using Platform.SchemaRunner;
namespace Platform.UnitTests;

public sealed class ArchivePrivateFileTests
{
    [Fact]
    public void DescriptorReadsRejectLinksAncestorsModesAndSpecialFiles()
    {
        if (!OperatingSystem.IsLinux()) return;
        string directory = Path.Combine(Path.GetTempPath(), "teleg-private-file-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        void Run(string command, params string[] arguments) { var info = new ProcessStartInfo(command) { UseShellExecute = false }; foreach (string argument in arguments) info.ArgumentList.Add(argument); using var process = Process.Start(info)!; process.WaitForExit(); Assert.Equal(0, process.ExitCode); }
        try
        {
            string path = Path.Combine(directory, "original"), link = Path.Combine(directory, "link"), alias = directory + "-alias";
            File.WriteAllBytes(path, [1, 2, 3]); File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); Assert.Equal(new byte[] { 1, 2, 3 }, ArchivePrivateFile.Read(path));
            Assert.Throws<MigrationRejectedException>(() => ArchivePrivateFile.Read(path, 4)); Assert.Throws<MigrationRejectedException>(() => ArchivePrivateFile.Read(path, 1, 2));
            File.CreateSymbolicLink(link, path); Assert.Throws<MigrationRejectedException>(() => ArchivePrivateFile.Read(link)); File.Delete(link);
            Directory.CreateSymbolicLink(alias, directory); try { Assert.Throws<MigrationRejectedException>(() => ArchivePrivateFile.Read(Path.Combine(alias, "original"))); } finally { Directory.Delete(alias); }
            Run("ln", path, link); Assert.Throws<MigrationRejectedException>(() => ArchivePrivateFile.Read(path)); File.Delete(link);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead); Assert.Throws<MigrationRejectedException>(() => ArchivePrivateFile.Read(path)); File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Run("mkfifo", link); Assert.Throws<MigrationRejectedException>(() => ArchivePrivateFile.Read(link)); File.Delete(link);
            Assert.Throws<MigrationRejectedException>(() => ArchivePrivateFile.Read(directory)); Assert.Throws<MigrationRejectedException>(() => ArchivePrivateFile.Read(Path.Combine(directory, "..", "missing")));
        }
        finally { Directory.Delete(directory, true); }
    }
}
