using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Platform.Security;

namespace Platform.Persistence;

public sealed class SqliteDeliveryStore(SqliteSessionFactory sessions, SecretSnapshot secrets)
{
    public Task<Guid?> PrepareAsync(BrandContext context, JobLease lease, CancellationToken ct = default)
    {
        Guid order = Require(lease, JobKind.PrepareManifest, context);
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            var state = await ReadAsync(tx, context, order, token);
            var renewed = await OutboxStore.RenewSqliteAsync(tx, lease, TimeSpan.FromSeconds(30), token);
            if (state.Kind != "product_purchase" || state.Payment is not ("paid" or "not_required") || state.Held || state.Review)
                throw new UnauthorizedAccessException("Order is unavailable for manifest preparation.");
            if (state.Generation is null)
            {
                using var manifest = await BuildAsync(tx, context, order, state.Quantity, false, token);
                bool unresolved = await SqliteSessionFactory.StepEfAsync(tx, false, (data, cancel) => data.Data.Set<Entities.SupplierRequest>().AnyAsync(row => row.OrderId == order && row.Status != "succeeded" && row.Status != "resolved", cancel), token);
                if (!manifest.Complete || unresolved)
                {
                    await OutboxStore.RetrySqliteAsync(tx, renewed, TimeSpan.FromSeconds(5), 100, JobError.Busy, token);
                    return (Guid?)null;
                }
                Guid generation = Guid.CreateVersion7();
                await OrderAsync(tx, context, order, row => row.ReleaseAuthorizedAt == null, (row, now) => { row.DeliveryGeneration = generation; row.ManifestAssetRef = Asset(order, generation); row.ManifestDigest = SHA256.HashData(manifest.Bytes); row.ManifestPreparedAt = now; row.ReleaseAuthorizedAt = now; row.FulfillmentStatus = "ready"; row.Revision++; row.UpdatedAt = now; }, token);
                await PartsAsync(tx, context, order, row => row.Status == "acquired", (row, now) => { row.Status = "ready"; row.ManifestGeneration = generation; }, token);
                await SqliteDiscountClaimStore.TransitionAsync(tx, context, order, true, token);
                state = await ReadAsync(tx, context, order, token);
            }
            using var frozen = await BuildAsync(tx, context, order, state.Quantity, true, token);
            Verify(state, order, frozen);
            long? target = await SqliteSessionFactory.StepEfAsync(tx, false, (data, cancel) => data.Data.Set<Entities.TelegramDelivery>().Where(row => row.OrderId == order && row.UserId == state.Member && row.BrandId == context.BrandId && row.Purpose == "order_status" && row.Status == "succeeded").OrderBy(row => row.CreatedAt).ThenBy(row => row.Id).Select(row => row.TelegramMessageId).FirstOrDefaultAsync(cancel), token);
            Guid? delivery = null;
            if (target is long targetMessage && !frozen.IsFile)
                delivery = await EnqueueAsync(tx, context, state, order, targetMessage, token);
            await OutboxStore.CompleteSqliteAsync(tx, renewed, token);
            return delivery;
        }, ct);
    }

    public Task<DeliveryDispatch?> BeginAsync(BrandContext context, JobLease lease, CancellationToken ct = default)
    {
        Guid delivery = Require(lease, JobKind.SendTelegram, context);
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            var intent = await ReadIntentAsync(tx, delivery, token);
            var state = await ReadAsync(tx, context, intent.Order, token);
            var renewed = await OutboxStore.RenewSqliteAsync(tx, lease, TimeSpan.FromSeconds(30), token);
            if (intent.Status == "succeeded")
            {
                await OutboxStore.CompleteSqliteAsync(tx, renewed, token);
                return (DeliveryDispatch?)null;
            }
            bool prior = await SqliteSessionFactory.StepEfAsync(tx, false, (data, cancel) => data.Data.Set<Entities.TelegramDelivery>().AnyAsync(row => row.OrderId == intent.Order && row.Purpose == "order_credentials" && row.DeliveryGeneration == intent.Generation && row.Status == "succeeded" && row.Id != delivery, cancel), token);
            if (prior)
            {
                await MessageAsync(tx, context, delivery, row => true, (row, now) => { row.Status = "cancelled"; row.LastErrorCode = "known_success"; }, token);
                await OutboxStore.CompleteSqliteAsync(tx, renewed, token); return (DeliveryDispatch?)null;
            }
            if (intent.Status is not ("pending" or "retry") || intent.Generation != state.Generation ||
                state.Status is not ("ready" or "delivering" or "completed") || !state.Released ||
                state.Held || state.Review || state.Blocked)
                throw new UnauthorizedAccessException("Credential delivery authority changed.");
            if (intent.Target is long target)
            {
                var metadata = await SqliteSessionFactory.StepEfAsync(tx, false, (data, cancel) => data.Data.Set<Entities.TelegramDelivery>().Where(row => row.OrderId == intent.Order && row.Purpose == "order_status" && row.TargetMessageId == target).Select(row => new { row.Status, row.LastErrorCode }).Take(1001).ToListAsync(cancel), token);
                if (metadata.Count > 1000) throw new InvalidOperationException("Status message bound exceeded.");
                foreach (var row in metadata)
                {
                    if (row.Status == "uncertain" || row.LastErrorCode == "remote_outcome_unknown")
                    {
                        await OutboxStore.FailSqliteAsync(tx, renewed, JobError.CredentialRecovery, token);
                        await MessageAsync(tx, context, delivery, value => true, (value, now) => { value.Status = "failed"; value.LastErrorCode = "credential_recovery"; }, token); return (DeliveryDispatch?)null;
                    }
                    if (row.Status == "leased") { await OutboxStore.RetrySqliteAsync(tx, renewed, TimeSpan.FromSeconds(1), 100, JobError.Busy, token); return (DeliveryDispatch?)null; }
                }
            }
            using var frozen = await BuildAsync(tx, context, intent.Order, state.Quantity, true, token);
            Verify(state, intent.Order, frozen);
            if (intent.Target is not null && frozen.IsFile)
            {
                await OutboxStore.FailSqliteAsync(tx, renewed, JobError.CredentialRecovery, token);
                await MessageAsync(tx, context, delivery, row => true, (row, now) => { row.Status = "failed"; row.LastErrorCode = "credential_recovery"; }, token);
                return (DeliveryDispatch?)null;
            }
            await MessageAsync(tx, context, delivery, row => row.Status == "pending" || row.Status == "retry", (row, now) => { row.Status = "leased"; row.Attempts = renewed.Attempt; row.LastAttemptAt = now; }, token);
            await OrderAsync(tx, context, intent.Order, row => true, (row, now) => { if (row.FulfillmentStatus == "ready") { row.FulfillmentStatus = "delivering"; row.Revision++; row.UpdatedAt = now; } }, token);
            return new DeliveryDispatch(delivery, intent.Order, intent.Generation, context.BotId,
                state.Telegram, intent.Target, frozen.IsFile, frozen.Payload.ToArray());
        }, ct);
    }

    public Task RecordOutcomeAsync(BrandContext context, JobLease lease, DeliveryOutcome outcome, CancellationToken ct = default)
    {
        Guid delivery = Require(lease, JobKind.SendTelegram, context);
        if (!Enum.IsDefined(outcome.Kind)) throw new ArgumentException("Invalid delivery outcome.");
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            var intent = await ReadIntentAsync(tx, delivery, token);
            var state = await ReadAsync(tx, context, intent.Order, token);
            if (intent.Status == "succeeded") return 0;
            if (intent.Status != "leased" || intent.Attempt != lease.Attempt ||
                intent.Generation != state.Generation || !state.Released || state.Held || state.Review)
                throw new UnauthorizedAccessException("Delivery lease no longer owns this intent.");
            bool success = outcome.Kind == DeliveryOutcomeKind.Succeeded && outcome.MessageId is > 0 &&
                (intent.Target is null || intent.Target == outcome.MessageId);
            bool retry = intent.Target is not null && outcome.Kind is (DeliveryOutcomeKind.Unknown or DeliveryOutcomeKind.Retry);
            string status = success ? "succeeded" : retry ? "retry" :
                outcome.Kind == DeliveryOutcomeKind.Unknown ? "uncertain" : "failed";
            int changed = await JobsAsync(tx, context, intent.Order, row => row.Id == lease.Id && row.TelegramDeliveryId == delivery && row.Attempts == lease.Attempt && row.Status == "leased" && row.LockedBy == lease.Worker.ToString("D") && row.LeaseUntil > Platform.Persistence.Configurations.EfValues.UtcNow(), (row, now) => { row.Status = success ? "succeeded" : retry ? "retry" : "failed"; row.LockedBy = null; row.LeaseUntil = null; row.AvailableAt = now.AddSeconds(5); row.CompletedAt = retry ? null : now; row.LastErrorCode = success ? "known_success" : status == "uncertain" ? "remote_outcome_unknown" : "credential_recovery"; }, token);
            if (changed != 1) throw new UnauthorizedAccessException("Concurrent delivery modification.");
            await MessageAsync(tx, context, delivery, row => row.Status == "leased", (row, now) => { row.Status = status; row.TelegramMessageId = success ? outcome.MessageId : null; row.SentAt = success ? now : null; row.LastErrorCode = success ? "known_success" : status == "uncertain" ? "remote_outcome_unknown" : "credential_recovery"; row.NextAttemptAt = now.AddSeconds(5); }, token);
            if (success)
            {
                await PartsAsync(tx, context, intent.Order, row => row.Status == "ready" && row.ManifestGeneration == intent.Generation, (row, now) => { row.Status = "released"; row.CompletedAt = now; }, token);
                await OrderAsync(tx, context, intent.Order, row => true, (row, now) => { row.FulfillmentStatus = "completed"; row.Revision++; row.UpdatedAt = now; }, token);
            }
            return 0;
        }, ct);
    }

    public Task RecoverExpiredAsync(BrandContext context, Guid delivery, CancellationToken ct = default) =>
        sessions.RunAsync(true, async (_, tx, token) =>
        {
            var intent = await ReadIntentAsync(tx, delivery, token);
            var state = await ReadAsync(tx, context, intent.Order, token);
            if (!state.Released || state.Generation != intent.Generation || state.Held || state.Review)
                throw new UnauthorizedAccessException("Recovery authority changed.");
            int changed = await JobsAsync(tx, context, intent.Order, row => row.TelegramDeliveryId == delivery && row.Status == "leased" && row.LeaseUntil <= Platform.Persistence.Configurations.EfValues.UtcNow(), (row, now) => { row.Status = intent.Target is null ? "failed" : "retry"; row.LockedBy = null; row.LeaseUntil = null; row.AvailableAt = now; row.CompletedAt = intent.Target is null ? now : null; row.LastErrorCode = "remote_outcome_unknown"; }, token);
            if (changed == 1) await MessageAsync(tx, context, delivery, row => row.Status == "leased", (row, now) => { row.Status = intent.Target is null ? "uncertain" : "retry"; row.LastErrorCode = "remote_outcome_unknown"; }, token);
            return 0;
        }, ct);

    public Task<byte[]> ReadManifestAsync(BrandContext context, long actor, Guid order, CancellationToken ct = default) =>
        sessions.RunAsync(false, async (_, tx, token) =>
        {
            var state = await ReadAsync(tx, context, order, token);
            if (state.Telegram != actor || !state.Released || state.Held || state.Review || state.Blocked)
                throw new UnauthorizedAccessException("Manifest is unavailable.");
            using var manifest = await BuildAsync(tx, context, order, state.Quantity, true, token);
            Verify(state, order, manifest);
            return manifest.Bytes.ToArray();
        }, ct);

    private static async Task<Guid> EnqueueAsync(SqliteTransaction tx, BrandContext context, State state, Guid order,
        long target, CancellationToken ct)
    {
        Guid? existing = await SqliteSessionFactory.StepEfAsync(tx, false, (data, token) => data.Data.Set<Entities.TelegramDelivery>().Where(row => row.OrderId == order && row.Purpose == "order_credentials").OrderByDescending(row => row.PartNo).Select(row => (Guid?)row.Id).FirstOrDefaultAsync(token), ct);
        if (existing is Guid prior) return prior;
        Guid id = Guid.CreateVersion7();
        await SqliteSessionFactory.StepEfAsync(tx, true, async (data, token) => { await data.RequireOrderCommandAsync(context, order, token); data.Data.Add(new Entities.TelegramDelivery { Id = id, BrandId = context.BrandId, RecipientKind = "customer_dm", UserId = state.Member, Purpose = "order_credentials", Action = "edit", TargetMessageId = target, DeliveryGeneration = state.Generation, DedupeKey = "credentials:" + order.ToString("D") + ":" + state.Generation, OrderId = order, PartNo = 1 }); return true; }, ct);
        await OutboxStore.EnqueueSqliteAsync(tx, JobIntent.Telegram(context.BrandId, id), id, DateTime.UtcNow, ct);
        return id;
    }

    private static async Task<State> ReadAsync(SqliteTransaction tx, BrandContext context, Guid order, CancellationToken ct)
    {
        var result = await SqliteSessionFactory.StepEfAsync(tx, false, (data, token) => (from o in data.Data.Set<Entities.Order>() join u in data.Data.Set<Entities.User>() on o.UserId equals u.Id join b in data.Data.Set<Entities.Brand>() on u.BrandId equals b.Id where o.Id == order && b.Id == context.BrandId && b.BotId == context.BotId && b.Active select new State(o.UserId, o.Quantity, o.OrderKind, o.PaymentStatus, o.FulfillmentStatus, o.DeliveryGeneration, o.ManifestAssetRef, o.ManifestDigest, o.ReleaseAuthorizedAt != null, o.HoldAt != null, o.ReviewCode != null, u.TelegramId, u.BlockedAt != null || u.BannedAt != null)).SingleOrDefaultAsync(token), ct);
        return result ?? throw new UnauthorizedAccessException("Order is unavailable.");
    }
    private static async Task<Intent> ReadIntentAsync(SqliteTransaction tx, Guid id, CancellationToken ct)
    {
        var result = await SqliteSessionFactory.StepEfAsync(tx, false, (data, token) => data.Data.Set<Entities.TelegramDelivery>().Where(row => row.Id == id && row.Purpose == "order_credentials" && row.DeliveryGeneration != null).Select(row => new Intent(row.OrderId!.Value, row.DeliveryGeneration!.Value, row.TargetMessageId, row.Status, row.Attempts)).SingleOrDefaultAsync(token), ct);
        return result ?? throw new UnauthorizedAccessException("Delivery is unavailable.");
    }

    private async Task<Manifest> BuildAsync(SqliteTransaction tx, BrandContext context, Guid order, int quantity, bool frozen,
        CancellationToken ct)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true);
        var text = new StringBuilder();
        bool file = false;
        int count = 0;
        var cipher = new BrandCipher(secrets);
        writer.Write(1);
        writer.Write(quantity);
        while (true)
        {
            var rows = await SqliteSessionFactory.StepEfAsync(tx, false, (data, token) => data.Data.Set<Entities.Fulfillment>().Where(row => row.OrderId == order).OrderBy(row => row.UnitNo).Skip(count).Take(1000).Select(row => new { row.Id, row.UnitNo, row.Status, row.ContentKind, row.DeliveredContent, row.DeliveredContentKeyId, row.ManifestGeneration }).ToListAsync(token), ct);
            foreach (var row in rows)
            {
                if (row.UnitNo != count + 1 || row.ContentKind is null || row.DeliveredContent is null || row.DeliveredContentKeyId is null || (frozen ? row.Status is not ("ready" or "released") || row.ManifestGeneration == null : row.Status != "acquired")) return new([], [], false, true);
                byte[] plain = cipher.Decrypt(context, "fulfillments", row.Id, "delivered_content", row.DeliveredContentKeyId, row.DeliveredContent);
                try
                {
                    writer.Write(row.UnitNo); writer.Write(row.ContentKind); writer.Write(plain.Length); writer.Write(plain);
                    if (bytes.Length > 16777216) throw new InvalidOperationException("Manifest exceeds delivery limit.");
                    if (row.ContentKind == "file") file = true;
                    if (!file) { try { text.Append(row.UnitNo).Append(". ").Append(new UTF8Encoding(false, true).GetString(plain)).Append('\n'); } catch (DecoderFallbackException) { file = true; } if (text.Length > 4096) file = true; }
                }
                finally { CryptographicOperations.ZeroMemory(plain); }
                count++;
            }
            if (rows.Count < 1000) break;
        }
        byte[] content = bytes.ToArray();
        return new(content, file ? content.ToArray() : Encoding.UTF8.GetBytes(text.ToString()), count == quantity, file);
    }

    private static void Verify(State state, Guid order, Manifest manifest)
    {
        if (state.Status is not ("ready" or "delivering" or "completed") || !state.Released ||
            state.Generation is not Guid generation || !manifest.Complete || state.Asset != Asset(order, generation) ||
            state.Digest is null || !CryptographicOperations.FixedTimeEquals(state.Digest, SHA256.HashData(manifest.Bytes)))
            throw new UnauthorizedAccessException("Manifest is unavailable.");
    }
    private static string Asset(Guid order, Guid generation) => "manifest:" + order.ToString("D") + ":" + generation.ToString("D");
    private static Guid Require(JobLease lease, JobKind kind, BrandContext context) =>
        lease.Intent.Kind == kind && lease.Intent.BrandId == context.BrandId && lease.Intent.OriginId is Guid id
            ? id : throw new UnauthorizedAccessException("Job origin is unavailable.");

    private static Task<bool> MessageAsync(SqliteTransaction tx, BrandContext context, Guid id, System.Linq.Expressions.Expression<Func<Entities.TelegramDelivery, bool>> predicate, Action<Entities.TelegramDelivery, DateTime> change, CancellationToken ct) => SqliteSessionFactory.StepEfAsync(tx, true, async (data, token) => { var row = await data.Data.Set<Entities.TelegramDelivery>().Where(predicate).SingleOrDefaultAsync(row => row.Id == id, token) ?? throw new UnauthorizedAccessException("Concurrent delivery modification."); await data.RequireOrderCommandAsync(context, row.OrderId!.Value, token); change(row, DateTime.UtcNow); return true; }, ct);
    private static Task<bool> OrderAsync(SqliteTransaction tx, BrandContext context, Guid order, System.Linq.Expressions.Expression<Func<Entities.Order, bool>> predicate, Action<Entities.Order, DateTime> change, CancellationToken ct) => SqliteSessionFactory.StepEfAsync(tx, true, async (data, token) => { await data.RequireOrderCommandAsync(context, order, token); var row = await data.Data.Set<Entities.Order>().Where(predicate).SingleOrDefaultAsync(row => row.Id == order, token) ?? throw new UnauthorizedAccessException("Concurrent delivery modification."); change(row, DateTime.UtcNow); return true; }, ct);
    private static Task<int> JobsAsync(SqliteTransaction tx, BrandContext context, Guid order, System.Linq.Expressions.Expression<Func<Entities.Job, bool>> predicate, Action<Entities.Job, DateTime> change, CancellationToken ct) => SqliteSessionFactory.StepEfAsync(tx, true, async (data, token) => { await data.RequireOrderCommandAsync(context, order, token); var rows = await data.Data.Set<Entities.Job>().Where(predicate).Take(1001).ToListAsync(token); if (rows.Count > 1000) data.RejectScope("Delivery job bound exceeded."); DateTime now = DateTime.UtcNow; foreach (var batch in rows.Chunk(128)) { foreach (var row in batch) change(row, now); await data.FlushAsync(token, false); } return rows.Count; }, ct);
    private static async Task PartsAsync(SqliteTransaction tx, BrandContext context, Guid order, System.Linq.Expressions.Expression<Func<Entities.Fulfillment, bool>> predicate, Action<Entities.Fulfillment, DateTime> change, CancellationToken ct)
    {
        int offset = 0;
        while (true)
        {
            int count = await SqliteSessionFactory.StepEfAsync(tx, true, async (data, token) => { await data.RequireOrderCommandAsync(context, order, token); var rows = await data.Data.Set<Entities.Fulfillment>().Where(row => row.OrderId == order).OrderBy(row => row.UnitNo).Skip(offset).Take(128).ToListAsync(token); DateTime now = DateTime.UtcNow; foreach (var row in rows.Where(predicate.Compile())) change(row, now); return rows.Count; }, ct);
            if (count < 128) return; offset += count;
        }
    }

    private sealed class Manifest(byte[] bytes, byte[] payload, bool complete, bool file) : IDisposable
    {
        internal byte[] Bytes { get; } = bytes;
        internal byte[] Payload { get; } = payload;
        internal bool Complete { get; } = complete;
        internal bool IsFile { get; } = file;
        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(Bytes);
            CryptographicOperations.ZeroMemory(Payload);
        }
    }
    private sealed record State(Guid Member, int Quantity, string Kind, string Payment, string Status, Guid? Generation,
        string? Asset, byte[]? Digest, bool Released, bool Held, bool Review, long Telegram, bool Blocked);
    private sealed record Intent(Guid Order, Guid Generation, long? Target, string Status, long Attempt);
}
