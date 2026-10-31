using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Platform.Security;

/// <summary>Descriptor-relative, no-follow reads; no native ABI structs or secret-bearing native error messages.</summary>
internal sealed partial class LinuxSecretDirectory : IDisposable
{
    // Linux x64 fcntl.h flags. Unsupported operating systems/architectures fail closed.
    private const int NoFollow = 0x20000;
    private const int CloseOnExec = 0x80000;
    private const int DirectoryOnly = 0x10000;
    private const int NonBlocking = 0x800;
    private readonly SafeFileHandle root;

    public LinuxSecretDirectory(string directory)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new SecretConfigurationException(SecretConfigurationFailure.UnsafeFile);
        if (string.IsNullOrEmpty(directory) || !Path.IsPathFullyQualified(directory) || directory.Contains('\0'))
            throw new SecretConfigurationException(SecretConfigurationFailure.UnsafeFile);
        var parts = directory.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p is "." or ".."))
            throw new SecretConfigurationException(SecretConfigurationFailure.UnsafeFile);
        var handle = Own(Open("/", DirectoryOnly | NoFollow | CloseOnExec));
        try
        {
            foreach (var part in parts)
            {
                var next = Own(OpenAt(handle, part, DirectoryOnly | NoFollow | CloseOnExec));
                handle.Dispose();
                handle = next;
            }
            if ((File.GetUnixFileMode(handle) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                throw new SecretConfigurationException(SecretConfigurationFailure.UnsafeFile);
            root = handle;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            handle.Dispose();
            throw new SecretConfigurationException(SecretConfigurationFailure.UnsafeFile);
        }
        catch { handle.Dispose(); throw; }
    }

    public byte[] Read(string name, int maximumBytes)
    {
        if (!OperatingSystem.IsLinux()) throw new SecretConfigurationException(SecretConfigurationFailure.UnsafeFile);
        if (!SecretFiles.IsFileName(name)) throw new SecretConfigurationException(SecretConfigurationFailure.UnsafeFile);
        var bytes = new byte[maximumBytes + 1];
        try
        {
            using var handle = Own(OpenAt(root, name, NoFollow | CloseOnExec | NonBlocking));
            var mode = File.GetUnixFileMode(handle);
            var attributes = File.GetAttributes(handle);
            if ((mode & ~(UnixFileMode.UserRead | UnixFileMode.GroupRead)) != 0 || (mode & UnixFileMode.UserRead) == 0 ||
                (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new SecretConfigurationException(SecretConfigurationFailure.UnsafeFile);
            var length = RandomAccess.GetLength(handle);
            if (length is <= 0 || length > maximumBytes)
                throw new SecretConfigurationException(SecretConfigurationFailure.InvalidConfiguration);
            int read = 0;
            while (read < bytes.Length)
            {
                int count = RandomAccess.Read(handle, bytes.AsSpan(read), read);
                if (count == 0) break;
                read += count;
            }
            if (read != length || RandomAccess.GetLength(handle) != length || File.GetUnixFileMode(handle) != mode)
                throw new SecretConfigurationException(SecretConfigurationFailure.UnsafeFile);
            return bytes.AsSpan(0, read).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new SecretConfigurationException(SecretConfigurationFailure.UnavailableFile);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public void Dispose() => root.Dispose();

    private static SafeFileHandle Own(int descriptor) => descriptor < 0
        ? throw new SecretConfigurationException(SecretConfigurationFailure.UnavailableFile)
        : new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "openat", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenAt(SafeFileHandle directory, string path, int flags);
}
