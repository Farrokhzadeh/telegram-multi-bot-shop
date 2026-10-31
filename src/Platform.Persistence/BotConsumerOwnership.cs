using System.Buffers.Binary;
using System.Security.Cryptography;
using Platform.Security;

namespace Platform.Persistence;

public static class BotConsumerOwnership
{
    public static Task<bool> TryAcquireAsync(DbSession session, BrandContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        byte[] material = new byte[40];
        "telegram-consumer-v1"u8.CopyTo(material);
        context.BrandId.TryWriteBytes(material.AsSpan(24, 16), bigEndian: true, out _);
        byte[] digest = SHA256.HashData(material);
        return session.QuerySingleAsync(new DbStatement("SELECT pg_try_advisory_xact_lock($1)", DbValue.Bigint(BinaryPrimitives.ReadInt64BigEndian(digest))), row => row.GetBoolean(0), ct);
    }
}
