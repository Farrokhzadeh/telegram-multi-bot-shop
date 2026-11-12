using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Platform.Migration;
namespace Platform.SchemaRunner;

internal static partial class ArchivePrivateFile
{
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
    }
    internal static byte[] Read(string path, int minimum = 1, int maximum = 16777216)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64 || !Path.IsPathFullyQualified(path) || minimum < 0 || maximum < minimum || maximum > 16777216) throw new MigrationRejectedException();
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p is "." or "..")) throw new MigrationRejectedException();
        SafeFileHandle handle = Own(Open("/", 0xb0000)); byte[]? bytes = null;
        try
        {
            for (int i = 0; i < parts.Length; i++)
            {
                var next = Own(OpenAt(handle, parts[i], i == parts.Length - 1 ? 0xa0800 : 0xb0000)); handle.Dispose(); handle = next;
            }
            Status before = Check(handle, minimum, maximum); bytes = new byte[(int)before.Size]; int read = 0;
            while (read < bytes.Length) { int count = RandomAccess.Read(handle, bytes.AsSpan(read), read); if (count == 0) throw new MigrationRejectedException(); read += count; }
            Status after = Check(handle, minimum, maximum);
            if (before.Inode != after.Inode || before.Size != after.Size || before.Mode != after.Mode || before.Uid != after.Uid || before.ChangedSeconds != after.ChangedSeconds || before.ChangedNanos != after.ChangedNanos || before.ModifiedSeconds != after.ModifiedSeconds || before.ModifiedNanos != after.ModifiedNanos) throw new MigrationRejectedException();
            byte[] result = bytes; bytes = null; return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { throw new MigrationRejectedException(); }
        finally { handle.Dispose(); if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }
    internal static string ReadText(string path)
    {
        byte[] bytes = Read(path);
        try { return new System.Text.UTF8Encoding(false, true).GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static Status Check(SafeFileHandle handle, int minimum, int maximum)
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
}
