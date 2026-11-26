using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Platform.Migration;
namespace Platform.SchemaRunner;

internal static partial class ArchivePrivateFile
{
    internal readonly record struct FrozenIdentity(uint DeviceMajor, uint DeviceMinor, ulong Inode, ulong Size,
        long Changed, uint ChangedNanos, long Modified, uint ModifiedNanos);

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Status
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(16)] internal uint Links;
        [FieldOffset(20)] internal uint Uid;
        [FieldOffset(28)] internal ushort Mode;
        [FieldOffset(32)] internal ulong Inode;
        [FieldOffset(40)] internal ulong Size;
        [FieldOffset(96)] internal long ChangedSeconds;
        [FieldOffset(104)] internal uint ChangedNanos;
        [FieldOffset(112)] internal long ModifiedSeconds;
        [FieldOffset(120)] internal uint ModifiedNanos;
        [FieldOffset(136)] internal uint DeviceMajor;
        [FieldOffset(140)] internal uint DeviceMinor;
    }
    internal static byte[] Read(string path, int minimum = 1, int maximum = 16777216)
    {
        if (minimum < 0 || maximum < minimum || maximum > 16777216) throw new MigrationRejectedException();
        using var handle = OpenPrivate(path);
        byte[]? bytes = null;
        try
        {
            Status before = Check(handle, minimum, maximum); bytes = new byte[(int)before.Size]; int read = 0;
            while (read < bytes.Length) { int count = RandomAccess.Read(handle, bytes.AsSpan(read), read); if (count == 0) throw new MigrationRejectedException(); read += count; }
            Status after = Check(handle, minimum, maximum);
            if (before.Inode != after.Inode || before.Size != after.Size || before.Mode != after.Mode || before.Uid != after.Uid || before.ChangedSeconds != after.ChangedSeconds || before.ChangedNanos != after.ChangedNanos || before.ModifiedSeconds != after.ModifiedSeconds || before.ModifiedNanos != after.ModifiedNanos) throw new MigrationRejectedException();
            byte[] result = bytes; bytes = null; return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { throw new MigrationRejectedException(); }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }
    internal static string ReadText(string path)
    {
        byte[] bytes = Read(path);
        try { return new System.Text.UTF8Encoding(false, true).GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    internal static FrozenIdentity FrozenSqlite(string path)
    {
        using var handle = OpenPrivate(path, requirePrivateParent: true);
        Status value = Check(handle, 100, long.MaxValue);
        Span<byte> header = stackalloc byte[20];
        if ((value.Mode & 0x80) != 0 || RandomAccess.Read(handle, header, 0) != header.Length ||
            !header[..16].SequenceEqual("SQLite format 3\0"u8) || header[18] != 1 || header[19] != 1)
            throw new MigrationRejectedException();
        foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
        {
            var sidecar = new FileInfo(path + suffix);
            if (sidecar.Exists || sidecar.LinkTarget is not null || Directory.Exists(sidecar.FullName))
                throw new MigrationRejectedException();
        }
        return Identity(value);
    }

    internal static FrozenIdentity FrozenAsset(string path)
    {
        using var handle = OpenPrivate(path, requirePrivateParent: true);
        Status value = Check(handle, 0, 16777216);
        if ((value.Mode & 0x80) != 0) throw new MigrationRejectedException();
        return Identity(value);
    }

    private static FrozenIdentity Identity(Status value) =>
        new(value.DeviceMajor, value.DeviceMinor, value.Inode, value.Size, value.ChangedSeconds, value.ChangedNanos, value.ModifiedSeconds, value.ModifiedNanos);

    internal static bool RequireSqliteSource(string path)
    {
        using var handle = OpenPrivate(path, requirePrivateParent: true);
        Check(handle, 100, long.MaxValue);
        Span<byte> header = stackalloc byte[20];
        if (RandomAccess.Read(handle, header, 0) != header.Length || !header[..16].SequenceEqual("SQLite format 3\0"u8) ||
            header[18] != header[19] || header[18] is not (1 or 2)) throw new MigrationRejectedException();
        return header[18] == 2;
    }

    internal static void RequireDirectory(string path)
    {
        using var handle = OpenPrivate(Path.Combine(path, "check"), requirePrivateParent: true, parentOnly: true);
    }

    internal static void FlushDirectory(string path)
    {
        using var handle = OpenPrivate(Path.Combine(path, "check"), requirePrivateParent: true, parentOnly: true);
        if (Sync(handle) != 0) throw new MigrationRejectedException();
    }

    internal static void Publish(string pending, string path)
    {
        if (Rename(-100, pending, -100, path, 1) != 0) throw new MigrationRejectedException();
        FlushDirectory(Path.GetDirectoryName(path)!);
    }

    internal static void RequireFile(string path)
    {
        using var handle = OpenPrivate(path, requirePrivateParent: true);
        Check(handle, 0, long.MaxValue);
    }

    internal static async Task<byte[]> HashRuntimeDatabaseAsync(string path, int version, CancellationToken ct)
    {
        using var handle = OpenPrivate(path, requirePrivateParent: true);
        Status before = Check(handle, 100, long.MaxValue);
        byte[] header = new byte[100];
        if (RandomAccess.Read(handle, header, 0) != header.Length || !header.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8) ||
            header[18] != 2 || header[19] != 2 ||
            System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(60, 4)) != version ||
            System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(68, 4)) != 0)
            throw new MigrationRejectedException();
        using var stream = new FileStream(handle, FileAccess.Read);
        byte[] digest = await SHA256.HashDataAsync(stream, ct);
        if (Identity(Check(handle, 100, long.MaxValue)) != Identity(before)) throw new MigrationRejectedException();
        return digest;
    }

    private static SafeFileHandle OpenPrivate(string path, bool requirePrivateParent = false, bool parentOnly = false)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64 || !Path.IsPathFullyQualified(path))
            throw new MigrationRejectedException();
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p is "." or "..")) throw new MigrationRejectedException();
        SafeFileHandle handle = Own(Open("/", 0xb0000));
        try
        {
            for (int i = 0; i < parts.Length; i++)
            {
                bool file = i == parts.Length - 1;
                if (file && requirePrivateParent &&
                    (Stat(handle, "", 0x1000, 0x7ff, out var parent) != 0 || (parent.Mask & 0x0a) != 0x0a || parent.Uid != User() || (parent.Mode & 0x3f) != 0))
                    throw new MigrationRejectedException();
                if (file && parentOnly) return handle;
                var next = Own(OpenAt(handle, parts[i], file ? 0xa0800 : 0xb0000));
                handle.Dispose();
                handle = next;
            }
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static Status Check(SafeFileHandle handle, long minimum, long maximum)
    {
        if (Stat(handle, "", 0x1000, 0x7ff, out var value) != 0 || (value.Mask & 0x3cf) != 0x3cf || value.Links != 1 || value.Uid != User() || (value.Mode & 0xf000) != 0x8000 || (value.Mode & 0x3f) != 0 || value.Size < (ulong)minimum || value.Size > (ulong)maximum) throw new MigrationRejectedException();
        return value;
    }
    private static SafeFileHandle Own(int descriptor) => descriptor < 0 ? throw new MigrationRejectedException() : new((IntPtr)descriptor, true);
    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);
    [LibraryImport("libc", EntryPoint = "openat", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenAt(SafeFileHandle directory, string path, int flags);
    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Stat(SafeFileHandle file, string path, int flags, uint mask, out Status status);
    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint User();
    [LibraryImport("libc", EntryPoint = "fsync")]
    private static partial int Sync(SafeFileHandle file);
    [LibraryImport("libc", EntryPoint = "renameat2", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Rename(int fromDirectory, string from, int toDirectory, string to, uint flags);
}
