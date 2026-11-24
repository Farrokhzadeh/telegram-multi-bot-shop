using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Configurations;
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
        DateTime now = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.Admin>().Where(row => row.Id == actor.AdminId).Select(row => EfValues.TransactionTime()).SingleAsync(token), ct);
        var position = cursor is null ? (AsOf: now, At: (DateTime?)null, Id: (Guid?)null) : Decode(cursor, binding, now);
        DateTime cutoff = now.AddHours(-4320);
        var rows = await session.RunEfStepAsync(false, (data, token) => (from e in data.Data.Set<Entities.BotActivityEvent>() join u in data.Data.Set<Entities.User>() on e.UserId equals u.Id where (filter.Brand == null || u.BrandId == filter.Brand) && (filter.Member == null || u.Id == filter.Member) && e.OccurredAt >= cutoff && e.OccurredAt <= position.AsOf && (filter.From == null || e.OccurredAt >= filter.From) && (filter.Until == null || e.OccurredAt < filter.Until) && (filter.Action == null || e.ActionCode == filter.Action) && (position.At == null || e.OccurredAt < position.At || e.OccurredAt == position.At && e.Id.CompareTo(position.Id!.Value) < 0) orderby e.OccurredAt descending, e.Id descending select new BotActivityMetadata(e.Id, u.BrandId, e.UserId, e.EventType, e.SourceKind, e.ActionCode, e.ProductId, e.OrderId, e.SupportTicketId, e.QuantitySelected, e.FlowRevision, e.OutcomeCode, e.OccurredAt)).Take(limit + 1).ToListAsync(token), ct);
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
    public void Dispose() { if (!disposed) { CryptographicOperations.ZeroMemory(cursorKey); disposed = true; } }
}
