using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Entities;
using Platform.Persistence.Configurations;
using Platform.Core;
using Platform.Persistence;

namespace Platform.Migration;

public enum TargetActivityOrigin { Telegram, Workflow, CouponLookup }
public sealed record TargetActivityRow(Guid Id, Guid Member, BotActivityAction Action, BotActivitySource Source, BotActivityOutcome Outcome,
    BotActivityContext Context, DateTime OccurredAt, TargetActivityOrigin Origin, Guid Identity)
{
    public string Key => Origin switch
    {
        TargetActivityOrigin.Telegram when Context.TelegramUpdate is long update => BotActivityRegistry.TelegramIdentity(update),
        TargetActivityOrigin.Workflow when Identity != Guid.Empty && Context.TelegramUpdate is null => "workflow:" + BotActivityRegistry.Get(Action).Code + ":" + Identity.ToString("N"),
        TargetActivityOrigin.CouponLookup when Identity != Guid.Empty && Action == BotActivityAction.DiscountsOpen && Source == BotActivitySource.Flow && Context == new BotActivityContext() && Outcome is BotActivityOutcome.Accepted or BotActivityOutcome.Rejected => "coupon_lookup:" + Identity.ToString("N"),
        _ => throw new MigrationRejectedException()
    };
    public override string ToString() => "TargetActivityRow [redacted]";
}
public sealed class VerifiedTargetActivityBatch
{
    private VerifiedTargetActivityBatch(SchemaManifest destination, Guid brand, DateTime snapshot, TargetActivityRow[] rows)
    { Destination = destination; Brand = brand; Snapshot = snapshot; Rows = Array.AsReadOnly(rows); }
    public SchemaManifest Destination { get; }
    public Guid Brand { get; }
    public DateTime Snapshot { get; }
    public IReadOnlyList<TargetActivityRow> Rows { get; }
    public static byte[] Encode(SchemaManifest destination, Guid brand, DateTime snapshot, IEnumerable<TargetActivityRow> rows) => Canonical(destination, brand, snapshot, Freeze(rows));
    public static VerifiedTargetActivityBatch Open(SchemaManifest destination, Guid brand, DateTime snapshot, IEnumerable<TargetActivityRow> rows, ReadOnlySpan<byte> expectedDigest)
    {
        var frozen = Freeze(rows); byte[] bytes = Canonical(destination, brand, snapshot, frozen);
        if (expectedDigest.Length != 32 || !CryptographicOperations.FixedTimeEquals(expectedDigest, SHA256.HashData(bytes))) throw new MigrationRejectedException();
        return new(destination, brand, snapshot, frozen);
    }
    private static TargetActivityRow[] Freeze(IEnumerable<TargetActivityRow> rows)
    { var frozen = rows.Take(501).ToArray(); if (frozen.Length is < 1 or > 500 || frozen.Any(r => r is null)) throw new MigrationRejectedException(); return frozen; }
    private static byte[] Canonical(SchemaManifest destination, Guid brand, DateTime snapshot, TargetActivityRow[] rows)
    {
        destination.Validate();
        if (destination.SqlSha256 != SchemaRunner.BaselineSqlSha256 || brand == Guid.Empty || snapshot.Kind != DateTimeKind.Utc || snapshot == DateTime.MinValue || snapshot == DateTime.MaxValue ||
            rows.Select(r => r.Id).Distinct().Count() != rows.Length || rows.Select(r => (r.Member, r.Key)).Distinct().Count() != rows.Length) throw new MigrationRejectedException();
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write("SHOP-TARGET-ACTIVITY-1"); writer.Write(BotActivityRegistry.Version); writer.Write(destination.ToFileText()); writer.Write(brand.ToByteArray()); writer.Write(snapshot.Ticks); writer.Write(rows.Length);
        foreach (var row in rows)
        {
            BotActivityRegistry.Validate(row.Action, row.Source, row.Outcome, row.Context);
            if (row.Id == Guid.Empty || row.Member == Guid.Empty || row.OccurredAt.Kind != DateTimeKind.Utc || row.OccurredAt == DateTime.MinValue || row.OccurredAt > snapshot || !Enum.IsDefined(row.Origin) ||
                (row.Origin == TargetActivityOrigin.Telegram && row.Identity != Guid.Empty)) throw new MigrationRejectedException();
            if (row.Origin == TargetActivityOrigin.Workflow)
            {
                if (row.Action < BotActivityAction.CheckoutCreated && row.Action is not (BotActivityAction.QuantityEnter or BotActivityAction.PolicyCheck) || row.Action == BotActivityAction.QuantityRejected ||
                    (row.Action == BotActivityAction.PolicyCheck && (row.Source != BotActivitySource.System || row.Outcome is not (BotActivityOutcome.Accepted or BotActivityOutcome.Blocked) || row.Context != new BotActivityContext())) ||
                    (row.Action == BotActivityAction.QuantityEnter && (row.Source != BotActivitySource.Flow || row.Outcome != BotActivityOutcome.Accepted || row.Context.Order is not null || row.Context.Ticket is not null || row.Context.FlowRevision is not null)) ||
                    (row.Action == BotActivityAction.CheckoutCreated && (row.Source != BotActivitySource.Flow || row.Outcome != BotActivityOutcome.Accepted)) ||
                    (row.Action is BotActivityAction.CheckoutCreated or BotActivityAction.CheckoutFunded or BotActivityAction.CheckoutExpired or BotActivityAction.CheckoutReplaced && row.Identity != row.Context.Order)) throw new MigrationRejectedException();
                if (row.Action is BotActivityAction.FlowExpired or BotActivityAction.FlowAbandoned)
                {
                    byte[] identity = new byte[24]; row.Member.TryWriteBytes(identity.AsSpan(0, 16)); BinaryPrimitives.WriteInt64BigEndian(identity.AsSpan(16), row.Context.FlowRevision!.Value);
                    if (row.Source != BotActivitySource.System || row.Outcome != BotActivityOutcome.Cancelled || row.Identity != new Guid(SHA256.HashData(identity).AsSpan(0, 16))) throw new MigrationRejectedException();
                }
            }
            writer.Write(row.Id.ToByteArray()); writer.Write(row.Member.ToByteArray()); writer.Write((int)row.Action); writer.Write((int)row.Source); writer.Write((int)row.Outcome);
            writer.Write(row.Context.Product?.ToByteArray() ?? new byte[16]); writer.Write(row.Context.Order?.ToByteArray() ?? new byte[16]); writer.Write(row.Context.Ticket?.ToByteArray() ?? new byte[16]);
            writer.Write(row.Context.Quantity ?? -1); writer.Write(row.Context.FlowRevision ?? -1); writer.Write(row.Context.TelegramUpdate ?? -1);
            writer.Write(row.OccurredAt.Ticks); writer.Write((int)row.Origin); writer.Write(row.Identity.ToByteArray()); writer.Write(row.Key);
        }
        writer.Flush(); return stream.ToArray();
    }
    public override string ToString() => "VerifiedTargetActivityBatch [redacted]";
}
public sealed record TargetActivityImportResult(int Inserted, int Replayed, int Expired);
public sealed class TargetActivityImportWorkflow(DbSessionFactory sessions)
{
    public async Task<TargetActivityImportResult> ImportAsync(VerifiedTargetActivityBatch batch, CancellationToken ct = default)
    {
        var destination = batch.Destination; destination.Validate();
        if (Environment.GetEnvironmentVariable("SHOP_TEST_ALLOW_DATABASE_CREATION") != "1" || destination.SqlSha256 != SchemaRunner.BaselineSqlSha256) throw new MigrationRejectedException();
        await using var session = await sessions.BeginAsync(ct);
        if (!session.MatchesEndpoint(destination.Host, destination.Port, destination.Database, destination.Username)) throw new MigrationRejectedException();
        bool permitted = await session.QuerySingleAsync(new DbStatement("""
            SELECT current_database()=$1 AND current_user=$2 AND current_setting('server_version_num')::integer=$3
                AND EXISTS(SELECT 1 FROM pg_roles WHERE rolname='shop_activity_import' AND pg_has_role(current_user,oid,'MEMBER'))
                AND coalesce(obj_description('shop'::regnamespace,'pg_namespace'),'') LIKE $4
            """, DbValue.Text(destination.Database), DbValue.Text(destination.Username), DbValue.Integer(destination.ServerVersion), DbValue.Text("shop-schema|0001|" + destination.SqlSha256 + "|%")), r => r.GetBoolean(0), ct);
        if (!permitted) throw new MigrationRejectedException();
        DateTime now = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Brand>().Where(row => row.Id == batch.Brand).Select(row => EfValues.TransactionTime()).SingleAsync(token), ct);
        if (batch.Snapshot > now) throw new MigrationRejectedException();
        foreach (Guid member in batch.Rows.Select(r => r.Member).Distinct().Order())
            if (await session.RunEfStepAsync(false, (data, token) => data.LockUserAsync(member, batch.Brand, token), ct) != 1) throw new MigrationRejectedException();
        int inserted = 0, replayed = 0, expired = 0;
        foreach (var row in batch.Rows)
        {
            var definition = BotActivityRegistry.Get(row.Action); var links = row.Context;
            var value = new BotActivityEvent { Id = row.Id, UserId = row.Member, EventType = definition.EventType, SourceKind = BotActivityRegistry.Source(row.Source), ActionCode = definition.Code, ProductId = links.Product, OrderId = links.Order, SupportTicketId = links.Ticket, QuantitySelected = links.Quantity, FlowRevision = links.FlowRevision, TelegramUpdateId = links.TelegramUpdate, OutcomeCode = BotActivityRegistry.Outcome(row.Outcome), IdempotencyKey = row.Key, OccurredAt = new DateTime(row.OccurredAt.Ticks - row.OccurredAt.Ticks % 10, DateTimeKind.Utc) };
            bool origin = await session.RunEfStepAsync(false, async (data, token) =>
            {
                var owner = await (from u in data.Data.Set<User>() join b in data.Data.Set<Brand>() on u.BrandId equals b.Id where u.Id == row.Member && (links.Product == null || data.Data.Set<BrandProduct>().Any(l => l.BrandId == u.BrandId && l.ProductId == links.Product)) && (links.Order == null || data.Data.Set<Order>().Any(o => o.Id == links.Order && o.UserId == u.Id && (links.Product == null || o.ProductId == links.Product))) && (links.Ticket == null || data.Data.Set<SupportTicket>().Any(t => t.Id == links.Ticket && t.UserId == u.Id)) select new { u.BrandId, u.FlowRevision, b.TelegramUpdateHighwater, b.TelegramUpdateWindow }).SingleOrDefaultAsync(token);
                if (owner is null || links.TelegramUpdate is long update && !ActivityRetentionQueries.Seen(owner.TelegramUpdateHighwater, owner.TelegramUpdateWindow, update)) return false;
                if (definition.Code is "flow_expired" or "flow_abandoned" && !(links.FlowRevision is long revision && owner.FlowRevision > revision)) return false;
                if (definition.Code is "checkout_created" or "checkout_funded" or "checkout_expired" or "checkout_replaced")
                {
                    if (links.Product == null || links.Quantity == null || !await data.Data.Set<Order>().AnyAsync(o => o.Id == links.Order && o.UserId == row.Member && o.ProductId == links.Product && o.Quantity == links.Quantity && (definition.Code == "checkout_created" && o.CreatedAt == value.OccurredAt || definition.Code == "checkout_funded" && (o.PaymentStatus == "paid" || o.PaymentStatus == "not_required") && o.PaymentConfirmedAt == value.OccurredAt || definition.Code == "checkout_expired" && o.ResolutionKind == "unpaid_expiry" && o.ResolutionAt == value.OccurredAt || definition.Code == "checkout_replaced" && o.ResolutionKind == "replaced" && o.ResolutionAt == value.OccurredAt), token)) return false;
                }
                return definition.EventType != "payment" || await ActivityRetentionQueries.PaymentAsync(data, value, owner.BrandId, token);
            }, ct);
            if (!origin) throw new MigrationRejectedException();
            if (row.OccurredAt < now.AddHours(-4320)) { expired++; continue; }
            bool added = await session.RunEfStepAsync(true, async (data, token) =>
            {
                data.RequireImportCommand(); var prior = await data.Data.Set<BotActivityEvent>().SingleOrDefaultAsync(e => e.UserId == row.Member && e.IdempotencyKey == row.Key, token);
                if (prior is null) { data.Data.Add(value); return true; }
                if (prior.Id != value.Id || prior.UserId != value.UserId || prior.EventType != value.EventType || prior.SourceKind != value.SourceKind || prior.ActionCode != value.ActionCode || prior.ProductId != value.ProductId || prior.OrderId != value.OrderId || prior.SupportTicketId != value.SupportTicketId || prior.QuantitySelected != value.QuantitySelected || prior.FlowRevision != value.FlowRevision || prior.TelegramUpdateId != value.TelegramUpdateId || prior.OutcomeCode != value.OutcomeCode || prior.IdempotencyKey != value.IdempotencyKey || prior.OccurredAt != value.OccurredAt) throw new MigrationRejectedException(); return false;
            }, ct);
            if (added) inserted++; else replayed++;
        }
        await session.CommitAsync(ct); return new(inserted, replayed, expired);
    }
}
