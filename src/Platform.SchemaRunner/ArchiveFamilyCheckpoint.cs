using System.Security.Cryptography;
using System.Text;
using Platform.Migration;
namespace Platform.SchemaRunner;

internal sealed record ArchiveFamilyCheckpointResult(long Count, long RecoveredFields);
internal static class ArchiveFamilyCheckpoint
{
    internal static async Task<ArchiveFamilyCheckpointResult> VerifyAsync(ArchiveFamilyCheckpointResult? saved, Func<Task<ArchiveFamilyCheckpointResult>> verify)
    {
        var actual = await verify();
        if (actual.Count < 0 || actual.RecoveredFields < 0 || saved is not null && saved != actual) throw new MigrationRejectedException();
        return actual;
    }
    internal static byte[] Create(int projection, long count, long fields, ReadOnlySpan<byte> inputs, ReadOnlySpan<byte> key)
    {
        if (projection < 0 || count < 0 || fields < 0 || inputs.Length != 32 || key.Length < 16) throw new MigrationRejectedException();
        using var stream = new MemoryStream(); using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) { writer.Write("teleg-archive-family-checkpoint-v1"); writer.Write(projection); writer.Write(count); writer.Write(fields); writer.Write(inputs); writer.Flush(); }
        byte[] body = stream.ToArray(), tag = HMACSHA256.HashData(key, body), result = new byte[body.Length + tag.Length]; body.CopyTo(result, 0); tag.CopyTo(result, body.Length); CryptographicOperations.ZeroMemory(body); CryptographicOperations.ZeroMemory(tag); return result;
    }
    internal static ArchiveFamilyCheckpointResult Open(byte[] encoded, int projection, ReadOnlySpan<byte> inputs, ReadOnlySpan<byte> key)
    {
        if (projection < 0 || inputs.Length != 32 || key.Length < 16 || encoded.Length is < 64 or > 1024) throw new MigrationRejectedException();
        int bodyLength = encoded.Length - 32; byte[] tag = HMACSHA256.HashData(key, encoded.AsSpan(0, bodyLength));
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(tag, encoded.AsSpan(bodyLength))) throw new MigrationRejectedException();
            using var stream = new MemoryStream(encoded, 0, bodyLength, false); using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            if (reader.ReadString() != "teleg-archive-family-checkpoint-v1" || reader.ReadInt32() != projection) throw new MigrationRejectedException();
            long count = reader.ReadInt64(), fields = reader.ReadInt64(); byte[] actual = reader.ReadBytes(32);
            if (count < 0 || fields < 0 || actual.Length != 32 || stream.Position != stream.Length || !CryptographicOperations.FixedTimeEquals(actual, inputs)) throw new MigrationRejectedException();
            return new(count, fields);
        }
        catch (Exception error) when (error is EndOfStreamException or IOException or ArgumentException) { throw new MigrationRejectedException(); }
        finally { CryptographicOperations.ZeroMemory(tag); }
    }
}
