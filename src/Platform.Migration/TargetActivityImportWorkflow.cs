using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using NpgsqlTypes;
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
                if (row.Action < BotActivityAction.CheckoutCreated && row.Action != BotActivityAction.QuantityEnter || row.Action == BotActivityAction.QuantityRejected ||
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
        DateTime now = await session.QuerySingleAsync(new DbStatement("SELECT CURRENT_TIMESTAMP"), r => r.GetUtc(0), ct);
        if (batch.Snapshot > now) throw new MigrationRejectedException();
        foreach (Guid member in batch.Rows.Select(r => r.Member).Distinct().Order())
            await session.QuerySingleAsync(new DbStatement("SELECT id FROM shop.users WHERE id=$1 AND brand_id=$2 FOR UPDATE", DbValue.Uuid(member), DbValue.Uuid(batch.Brand)), r => r.GetGuid(0), ct);
        int inserted = 0, replayed = 0, expired = 0;
        foreach (var row in batch.Rows)
        {
            var definition = BotActivityRegistry.Get(row.Action); var links = row.Context;
            var values = new[] { DbValue.Uuid(row.Id), DbValue.Uuid(row.Member), DbValue.Text(definition.EventType), DbValue.Text(BotActivityRegistry.Source(row.Source)), DbValue.Text(definition.Code),
                Id(links.Product), Id(links.Order), Id(links.Ticket), links.Quantity is int quantity ? DbValue.Integer(quantity) : DbValue.Null(NpgsqlDbType.Integer),
                links.FlowRevision is long revision ? DbValue.Bigint(revision) : DbValue.Null(NpgsqlDbType.Bigint), links.TelegramUpdate is long update ? DbValue.Bigint(update) : DbValue.Null(NpgsqlDbType.Bigint),
                DbValue.Text(BotActivityRegistry.Outcome(row.Outcome)), DbValue.Text(row.Key), DbValue.Utc(row.OccurredAt) };
            bool origin = await session.QuerySingleAsync(new DbStatement("""
                SELECT EXISTS(SELECT 1 FROM shop.users u JOIN shop.brands b ON b.id=u.brand_id WHERE u.id=$2
                    AND ($6 IS NULL OR EXISTS(SELECT 1 FROM shop.brand_products l WHERE l.brand_id=u.brand_id AND l.product_id=$6))
                    AND ($7 IS NULL OR EXISTS(SELECT 1 FROM shop.orders o WHERE o.id=$7 AND o.user_id=u.id AND ($6 IS NULL OR o.product_id=$6)))
                    AND ($8 IS NULL OR EXISTS(SELECT 1 FROM shop.support_tickets t WHERE t.id=$8 AND t.user_id=u.id))
                    AND ($11 IS NULL OR shop.telegram_update_seen(b.telegram_update_highwater,b.telegram_update_window,$11))
                    AND ($5 NOT IN ('flow_expired','flow_abandoned') OR u.flow_revision>$10)
                    AND ($5<>'checkout_created' OR EXISTS(SELECT 1 FROM shop.orders o WHERE o.id=$7 AND o.user_id=u.id AND o.product_id=$6 AND o.quantity=$9 AND o.created_at=$14))
                    AND ($5 NOT IN ('checkout_funded','checkout_expired','checkout_replaced') OR EXISTS(SELECT 1 FROM shop.orders o WHERE o.id=$7 AND o.user_id=u.id AND o.product_id=$6 AND o.quantity=$9
                        AND CASE $5 WHEN 'checkout_funded' THEN o.payment_status IN ('paid','not_required') AND o.payment_confirmed_at=$14
                            WHEN 'checkout_expired' THEN o.resolution_kind='unpaid_expiry' AND o.resolution_at=$14
                            WHEN 'checkout_replaced' THEN o.resolution_kind='replaced' AND o.resolution_at=$14 ELSE false END))
                    AND ($3<>'payment' OR shop.activity_payment_origin(ROW($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14)::shop.bot_activity_events)))
                """, values), r => r.GetBoolean(0), ct);
            if (!origin) throw new MigrationRejectedException();
            if (row.OccurredAt < now.AddHours(-4320)) { expired++; continue; }
            int count = await session.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.bot_activity_events(id,user_id,event_type,source_kind,action_code,product_id,order_id,support_ticket_id,quantity_selected,flow_revision,telegram_update_id,outcome_code,idempotency_key,occurred_at)
                VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14) ON CONFLICT(user_id,idempotency_key) DO NOTHING
                """, values), ct);
            if (count == 0)
            {
                bool same = await session.QuerySingleAsync(new DbStatement("""
                    SELECT EXISTS(SELECT 1 FROM shop.bot_activity_events e WHERE e.user_id=$2 AND e.idempotency_key=$13
                        AND ROW(e.id,e.user_id,e.event_type,e.source_kind,e.action_code,e.product_id,e.order_id,e.support_ticket_id,e.quantity_selected,e.flow_revision,e.telegram_update_id,e.outcome_code,e.idempotency_key,e.occurred_at)
                        IS NOT DISTINCT FROM ROW($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14))
                    """, values), r => r.GetBoolean(0), ct);
                if (!same) throw new MigrationRejectedException();
                replayed++;
            }
            else inserted++;
        }
        await session.CommitAsync(ct); return new(inserted, replayed, expired);
    }
    private static DbValue Id(Guid? value) => value is Guid id ? DbValue.Uuid(id) : DbValue.Null(NpgsqlDbType.Uuid);
}
