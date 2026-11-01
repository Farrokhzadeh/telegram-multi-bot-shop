using System.Security.Cryptography;
using System.Text;
using NpgsqlTypes;

namespace Platform.Persistence;

internal static class AdminCommand
{
    public static byte[] Hash(string kind, params string?[] values)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        { writer.Write(kind); foreach (string? value in values) { writer.Write(value is not null); if (value is not null) writer.Write(value); } }
        return SHA256.HashData(stream.ToArray());
    }
    public static void Revision(long value) { if (value < 0 || value == long.MaxValue) throw new ArgumentOutOfRangeException(nameof(value)); }
    public static void Page(int limit) { if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit)); }
    public static void Reason(string reason) { if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1024 || reason.Any(char.IsControl)) throw new ArgumentException("A bounded review reason is required."); }
    public static DbValue Text(string? value) => value is null ? DbValue.Null(NpgsqlDbType.Text) : DbValue.Text(value);
    public static DbValue Id(Guid? value) => value is Guid id ? DbValue.Uuid(id) : DbValue.Null(NpgsqlDbType.Uuid);
    public static DbValue Time(DateTime? value) => value is DateTime time ? DbValue.Utc(time) : DbValue.Null(NpgsqlDbType.TimestampTz);
}
public sealed record AdminPage<T>(T[] Items, Guid? Next);
