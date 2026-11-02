using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using NpgsqlTypes;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

public sealed record BotActivityFilter(Guid? Brand = null, Guid? Member = null, DateTime? From = null, DateTime? Until = null, string? Action = null);
public sealed record BotActivityMetadata(Guid Id, Guid BrandId, Guid MemberId, string EventType, string Source, string Action, Guid? ProductId, Guid? OrderId, Guid? TicketId, int? Quantity, long? FlowRevision, string Outcome, DateTime OccurredAt);
public sealed record BotActivityPage(BotActivityMetadata[] Items, string? Next, DateTime AsOf);
public sealed class BotActivityStore : IDisposable
{
    private readonly DbSessionFactory sessions;
    private readonly byte[] cursorKey;
    private bool disposed;
    public BotActivityStore(DbSessionFactory sessions, ReadOnlySpan<byte> cursorKey)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        if (cursorKey.Length != 32) throw new ArgumentException("Timeline cursor protection requires 32 bytes.");
        this.sessions = sessions; this.cursorKey = cursorKey.ToArray();
    }
    public async Task<BotActivityPage> ReadAsync(AuthenticatedAdmin actor, BotActivityFilter filter, string? cursor = null, int limit = 50, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(filter); ArgumentNullException.ThrowIfNull(actor);
        if (limit is < 1 or > 100 || filter.Brand == Guid.Empty || filter.Member == Guid.Empty || filter.From?.Kind is not (null or DateTimeKind.Utc) ||
            filter.Until?.Kind is not (null or DateTimeKind.Utc) || filter.From >= filter.Until) throw new ArgumentException("Invalid activity filters.");
        if (filter.Action is not null) _ = BotActivityRegistry.FromCode(filter.Action);
        byte[] binding = SHA256.HashData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"{actor.AdminId:N}|{actor.SessionId:N}|{filter.Brand:N}|{filter.Member:N}|{filter.From:O}|{filter.Until:O}|{filter.Action}|{limit}")));
        await using var session = await sessions.BeginAsync(ct);
        await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
        DateTime now = await session.QuerySingleAsync(new DbStatement("SELECT CURRENT_TIMESTAMP"), r => r.GetUtc(0), ct);
        var position = cursor is null ? (AsOf: now, At: (DateTime?)null, Id: (Guid?)null) : Decode(cursor, binding, now);
        var rows = await session.QueryManyAsync(new DbStatement("""
            SELECT e.id,u.brand_id,e.user_id,e.event_type,e.source_kind,e.action_code,e.product_id,e.order_id,e.support_ticket_id,e.quantity_selected,e.flow_revision,e.outcome_code,e.occurred_at
            FROM shop.bot_activity_events e JOIN shop.users u ON u.id=e.user_id
            WHERE ($1 IS NULL OR u.brand_id=$1) AND ($2 IS NULL OR u.id=$2)
                AND e.occurred_at>=CURRENT_TIMESTAMP-INTERVAL '4320 hours' AND e.occurred_at<=$3
                AND ($4 IS NULL OR e.occurred_at>=$4) AND ($5 IS NULL OR e.occurred_at<$5) AND ($6 IS NULL OR e.action_code=$6)
                AND ($7 IS NULL OR (e.occurred_at,e.id)<($7,$8))
            ORDER BY e.occurred_at DESC,e.id DESC LIMIT $9
            """, Id(filter.Brand), Id(filter.Member), DbValue.Utc(position.AsOf), Time(filter.From), Time(filter.Until), filter.Action is null ? DbValue.Null(NpgsqlDbType.Text) : DbValue.Text(filter.Action),
            Time(position.At), Id(position.Id), DbValue.Integer(limit + 1)), r => new BotActivityMetadata(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetString(4), r.GetString(5), Id(r, 6), Id(r, 7), Id(r, 8), r.IsDBNull(9) ? null : r.GetInt32(9), r.IsDBNull(10) ? null : r.GetInt64(10), r.GetString(11), r.GetUtc(12)), limit + 1, ct);
        var items = rows.Take(limit).ToArray();
        return new(items, rows.Count > limit ? Encode(position.AsOf, items[^1], binding) : null, position.AsOf);
    }
    private string Encode(DateTime asOf, BotActivityMetadata last, byte[] binding)
    {
        byte[] bytes = new byte[97]; bytes[0] = 1;
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(1), asOf.Ticks); BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(9), last.OccurredAt.Ticks);
        last.Id.TryWriteBytes(bytes.AsSpan(17, 16)); binding.CopyTo(bytes, 33);
        HMACSHA256.HashData(cursorKey, bytes.AsSpan(0, 65), bytes.AsSpan(65));
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
    private (DateTime AsOf, DateTime? At, Guid? Id) Decode(string cursor, byte[] binding, DateTime now)
    {
        if (cursor.Length != 130 || cursor.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) throw new ArgumentException("Invalid activity cursor.");
        byte[] bytes = Convert.FromBase64String(cursor.Replace('-', '+').Replace('_', '/') + "==");
        if (bytes.Length != 97 || bytes[0] != 1 || !CryptographicOperations.FixedTimeEquals(binding, bytes.AsSpan(33, 32)) ||
            !CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(cursorKey, bytes.AsSpan(0, 65)), bytes.AsSpan(65))) throw new ArgumentException("Invalid activity cursor.");
        long asOfTicks = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(1)), atTicks = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(9));
        if (asOfTicks < DateTime.MinValue.Ticks || asOfTicks > DateTime.MaxValue.Ticks || atTicks < DateTime.MinValue.Ticks || atTicks > asOfTicks) throw new ArgumentException("Invalid activity cursor.");
        DateTime asOf = new(asOfTicks, DateTimeKind.Utc);
        if (asOf > now || now - asOf > TimeSpan.FromMinutes(15)) throw new ArgumentException("Expired activity cursor.");
        return (asOf, new DateTime(atTicks, DateTimeKind.Utc), new Guid(bytes.AsSpan(17, 16)));
    }
    private static DbValue Id(Guid? value) => value is Guid id ? DbValue.Uuid(id) : DbValue.Null(NpgsqlDbType.Uuid);
    private static DbValue Time(DateTime? value) => value is DateTime time ? DbValue.Utc(time) : DbValue.Null(NpgsqlDbType.TimestampTz);
    private static Guid? Id(DbRow row, int column) => row.IsDBNull(column) ? null : row.GetGuid(column);
    public void Dispose() { if (!disposed) { CryptographicOperations.ZeroMemory(cursorKey); disposed = true; } }
}
