using System.Collections;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Platform.Migration;
namespace Platform.SchemaRunner;

internal static class ArchiveToolIdentity
{
    internal static byte[] Bind(string runtime, IReadOnlyDictionary<string, byte[]> files, IReadOnlyDictionary<string, string> environment)
    {
        if (string.IsNullOrEmpty(runtime) || files.Count == 0 || files.Any(x => string.IsNullOrEmpty(x.Key) || x.Value.Length != 32)) throw new MigrationRejectedException();
        using var stream = new MemoryStream();
        try
        {
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) { writer.Write("teleg-archive-runtime-v1"); writer.Write(runtime); writer.Write(files.Count); foreach (var item in files.OrderBy(x => x.Key, StringComparer.Ordinal)) { writer.Write(item.Key); writer.Write(item.Value); } writer.Write(environment.Count); foreach (var item in environment.OrderBy(x => x.Key, StringComparer.Ordinal)) { writer.Write(item.Key); writer.Write(item.Value); } writer.Flush(); }
            byte[] body = stream.ToArray(); try { return SHA256.HashData(body); } finally { CryptographicOperations.ZeroMemory(body); }
        }
        finally { if (stream.TryGetBuffer(out var buffer)) CryptographicOperations.ZeroMemory(buffer.AsSpan(0, (int)stream.Length)); }
    }
    internal static IEnumerable<string> MappedNativeFiles(IEnumerable<string> lines)
    {
        foreach (string line in lines)
        {
            string[] columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries); if (columns.Length < 6) continue;
            if (columns.Length == 7 && columns[^2] == "/memfd:doublemapper" && columns[^1] == "(deleted)") continue;
            if (columns[^1] == "(deleted)") throw new MigrationRejectedException();
            string path = columns[^1]; if (path.StartsWith('/') && path.Contains(".so", StringComparison.Ordinal)) yield return path;
        }
    }
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "Empty assembly locations are handled for single-file execution.")]
    internal static async Task<byte[]> CurrentAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) throw new MigrationRejectedException();
        byte[] warm = new byte[32]; _ = SHA256.HashData(warm); _ = HMACSHA256.HashData(warm, warm); using (var aes = new AesGcm(warm, 16)) { byte[] nonce = new byte[12], cipher = new byte[1], tag = new byte[16]; aes.Encrypt(nonce, new byte[1], cipher, tag); }
        string app = Path.GetFullPath(AppContext.BaseDirectory), process = Environment.ProcessPath ?? throw new MigrationRejectedException(), assembly = typeof(ReadOnlySourceArchiveVerification).Assembly.Location;
        if (!Path.IsPathFullyQualified(process) || !File.Exists(process)) throw new MigrationRejectedException();
        var paths = new HashSet<string>(StringComparer.Ordinal) { process };
        void DirectoryFiles(string directory)
        {
            if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory)) throw new MigrationRejectedException();
            foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(path);
                if (name.EndsWith(".dll", StringComparison.Ordinal) || name.Contains(".so", StringComparison.Ordinal) || name.EndsWith(".deps.json", StringComparison.Ordinal) || name.EndsWith(".runtimeconfig.json", StringComparison.Ordinal)) paths.Add(Path.GetFullPath(path));
            }
        }
        DirectoryFiles(app);
        if (!string.IsNullOrEmpty(assembly))
        {
            string runtimeDirectory = Path.GetFullPath(RuntimeEnvironment.GetRuntimeDirectory()); DirectoryFiles(runtimeDirectory);
            foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (loaded.IsDynamic) throw new MigrationRejectedException(); string location = loaded.Location;
                if (string.IsNullOrEmpty(location)) continue; location = Path.GetFullPath(location);
                if (!paths.Contains(location)) throw new MigrationRejectedException();
            }
        }
        foreach (string path in MappedNativeFiles(await File.ReadAllLinesAsync("/proc/self/maps", ct))) paths.Add(path);
        if (paths.Count is < 1 or > 4096) throw new MigrationRejectedException();
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string path in paths.Order(StringComparer.Ordinal)) { if (!File.Exists(path)) throw new MigrationRejectedException(); await using var stream = File.OpenRead(path); files.Add(path, await SHA256.HashDataAsync(stream, ct)); }
        var env = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry item in Environment.GetEnvironmentVariables())
        {
            string name = (string)item.Key; if (name.StartsWith("DOTNET_", StringComparison.Ordinal) || name.StartsWith("COMPlus_", StringComparison.Ordinal) || name is "TZ" or "LANG" or "LC_ALL") env.Add(name, item.Value?.ToString() ?? "");
        }
        return Bind(RuntimeInformation.FrameworkDescription + "|" + RuntimeInformation.OSArchitecture + "|" + Environment.Version, files, env);
    }
}
