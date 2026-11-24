using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

public enum DeliveryOutcomeKind { Succeeded, Retry, Uneditable, Unknown }
public sealed record DeliveryOutcome(DeliveryOutcomeKind Kind, long? MessageId = null);
public sealed class DeliveryDispatch : IDisposable
{
    internal DeliveryDispatch(Guid id, Guid order, Guid generation, long bot, long chat, long? target, bool file, byte[] content)
    { Id = id; Order = order; Generation = generation; BotId = bot; ChatId = chat; TargetMessageId = target; IsFile = file; this.content = content; }
    private readonly byte[] content;
    public Guid Id { get; }
    public Guid Order { get; }
    public Guid Generation { get; }
    public long BotId { get; }
    public long ChatId { get; }
    public long? TargetMessageId { get; }
    public bool IsFile { get; }
    public ReadOnlyMemory<byte> Content => content;
    public void Dispose() => CryptographicOperations.ZeroMemory(content);
    public override string ToString() => "DeliveryDispatch [redacted]";
}

public sealed class DeliveryStore(DbSessionFactory sessions, SecretSnapshot secrets)
{
    internal async Task<bool> HasCompleteAcquisitionAsync(DbSession session, BrandContext context, Guid order, int quantity, CancellationToken ct)
    {
        try { using Manifest manifest = await BuildAsync(session, context, order, quantity, false, ct); return manifest.Complete; }
        catch (ManifestNeedsRecoveryException) { return false; }
    }
    public async Task<Guid?> PrepareAsync(BrandContext context, JobLease lease, CancellationToken ct = default)
    {
        if (lease.Intent.Kind != JobKind.PrepareManifest || lease.Intent.BrandId != context.BrandId || lease.Intent.OriginId is not Guid order) throw new UnauthorizedAccessException();
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            State state = await LockAsync(session, context, order, ct);
            await OutboxStore.RenewAsync(session, lease, TimeSpan.FromSeconds(30), ct);
            if (state.Kind != "product_purchase" || state.Payment is not ("paid" or "not_required") || state.Held || state.Review) Reject(session);
            if (state.Generation is null)
            {
                using Manifest manifest = await BuildAsync(session, context, order, state.Quantity, false, ct);
                bool unresolved = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.SupplierRequest>().AnyAsync(row => row.OrderId == order && row.Status != "succeeded" && row.Status != "resolved", token), ct);
                if (!manifest.Complete || unresolved)
                {
                    await OutboxStore.RetryAsync(session, lease, TimeSpan.FromSeconds(5), 100, JobError.Busy, ct);
                    await session.CommitAsync(ct);
                    return null;
                }
                Guid generation = Guid.CreateVersion7();
                await PaymentSettlementStore.ChangeOrderAsync(session, order, (row, now) => { row.DeliveryGeneration = generation; row.ManifestAssetRef = Asset(order, generation); row.ManifestDigest = SHA256.HashData(manifest.Bytes); row.ManifestPreparedAt = now; row.ReleaseAuthorizedAt = now; row.FulfillmentStatus = "ready"; }, ct);
                await PartsAsync(session, context, order, row => row.Status == "acquired", (row, now) => { row.Status = "ready"; row.ManifestGeneration = generation; }, ct);
                Guid? coupon = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.DiscountUse>().Where(row => row.OrderId == order && row.Status == "held").Select(row => (Guid?)row.DiscountId).SingleOrDefaultAsync(token), ct);
                if (coupon is Guid id) await new PricingStore(sessions, secrets).TransitionClaimAsync(session, context, id, order, true, ct);
                state = await LockAsync(session, context, order, ct);
            }
            using Manifest frozen = await BuildAsync(session, context, order, state.Quantity, true, ct);
            Verify(state, order, frozen);
            long? anchor = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.TelegramDelivery>().Where(row => row.OrderId == order && row.UserId == state.Member && row.BrandId == context.BrandId && row.Purpose == "order_status" && row.Status == "succeeded").OrderBy(row => row.CreatedAt).ThenBy(row => row.Id).Select(row => row.TelegramMessageId).FirstOrDefaultAsync(token), ct);
            Guid? delivery = null;
            if (anchor is long target && !frozen.IsFile) delivery = await EnqueueAsync(session, context, state, order, target, null, ct);
            await OutboxStore.CompleteAsync(session, lease, ct);
            await session.CommitAsync(ct);
            return delivery;
        }
        catch (ManifestNeedsRecoveryException)
        {
            await SupplierQueries.OrderAsync(session, context, order, (row, now) => { if (row.ReleaseAuthorizedAt == null) { row.FulfillmentStatus = "requires_admin"; row.ReviewCode = "credential_recovery"; row.Revision++; } }, ct);
            await OutboxStore.FailAsync(session, lease, JobError.CredentialRecovery, ct);
            await session.CommitAsync(ct);
            return null;
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }

    internal static async Task RecordAnchorAsync(DbSession session, BrandContext context, long actor, Guid order, long message, CancellationToken ct)
    {
        await session.RunEfStepAsync(false, (data, token) => data.LockMemberAsync(context.BrandId, actor, token), ct);
        BrandUser member = await BrandUserStore.RequireAsync(session, context, actor, null, ct);
        bool valid = await session.RunEfStepAsync(false, (data, token) => (from u in data.Data.Set<Entities.User>() join o in data.Data.Set<Entities.Order>() on u.FlowOrderId equals o.Id where u.Id == member.Id && o.Id == order && o.UserId == u.Id && u.FlowAnchorMessageId == message && u.FlowExpiresAt > Platform.Persistence.Configurations.EfValues.UtcNow() select u.Id).AnyAsync(token), ct);
        if (!valid || message <= 0) Reject(session);
        string dedupe = "order-anchor:" + order.ToString("D") + ":" + message.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await session.RunEfStepAsync(true, async (data, token) => { await data.RequireOrderCommandAsync(context, order, token); if (!await data.Data.Set<Entities.TelegramDelivery>().AnyAsync(row => row.DedupeKey == dedupe, token)) data.Data.Add(new Entities.TelegramDelivery { Id = Guid.NewGuid(), BrandId = context.BrandId, RecipientKind = "customer_dm", UserId = member.Id, Purpose = "order_status", Action = "send", DedupeKey = dedupe, Status = "succeeded", TelegramMessageId = message, SentAt = await data.BrandClockAsync(context.BrandId, token), OrderId = order }); return true; }, ct);
    }

    public async Task<DeliveryDispatch?> BeginAsync(BrandContext context, JobLease lease, CancellationToken ct = default)
    {
        Guid delivery = Origin(context, lease);
        await using var session = await sessions.BeginAsync(ct);
        var intent = await ReadIntentAsync(session, delivery, ct);
        State state = await LockAsync(session, context, intent.Order, ct);
        await OutboxStore.RenewAsync(session, lease, TimeSpan.FromSeconds(30), ct);
        intent = await ReadIntentAsync(session, delivery, ct);
        if (intent.Status == "succeeded")
        { await OutboxStore.CompleteAsync(session, lease, ct); await session.CommitAsync(ct); return null; }
        bool alreadySent = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.TelegramDelivery>().AnyAsync(row => row.OrderId == intent.Order && row.Purpose == "order_credentials" && row.DeliveryGeneration == intent.Generation && row.Status == "succeeded" && row.Id != delivery, token), ct);
        if (alreadySent)
        {
            await MessageAsync(session, context, delivery, (row, now) => { if (row.Status is "pending" or "retry") { row.Status = "cancelled"; row.LastErrorCode = "known_success"; } }, ct);
            await OutboxStore.CompleteAsync(session, lease, ct);
            await session.CommitAsync(ct);
            return null;
        }
        if (intent.Status is not ("pending" or "retry") || intent.Generation != state.Generation || state.Status is not ("ready" or "delivering" or "completed") || !state.Released || state.Held || state.Review || state.Blocked) Reject(session);
        if (intent.Target is long statusTarget)
        {
            var metadata = await session.RunEfStepAsync(false, async (data, token) => { var query = data.Data.Set<Entities.TelegramDelivery>().Where(row => row.OrderId == intent.Order && row.Purpose == "order_status" && row.TargetMessageId == statusTarget); return (Active: await query.AnyAsync(row => row.Status == "leased", token), Unknown: await query.AnyAsync(row => row.LastErrorCode == "remote_outcome_unknown" || row.Status == "uncertain" || (row.Status == "leased" && data.Data.Set<Entities.Job>().Any(job => job.TelegramDeliveryId == row.Id && job.Status == "leased" && job.LeaseUntil <= Platform.Persistence.Configurations.EfValues.UtcNow())), token)); }, ct);
            if (metadata.Unknown)
            {
                await MessageAsync(session, context, delivery, (row, now) => { row.Status = "failed"; row.LastErrorCode = "credential_recovery"; }, ct);
                await OutboxStore.FailAsync(session, lease, JobError.CredentialRecovery, ct); await session.CommitAsync(ct); return null;
            }
            if (metadata.Active)
            {
                await JobsAsync(session, context, intent.Order, row => row.Id == lease.Id, (row, now) => { row.Status = "retry"; row.LockedBy = null; row.LeaseUntil = null; row.AvailableAt = now.AddSeconds(1); row.LastErrorCode = "metadata_in_flight"; }, ct);
                await session.CommitAsync(ct); return null;
            }
        }
        using Manifest manifest = await BuildAsync(session, context, intent.Order, state.Quantity, true, ct);
        Verify(state, intent.Order, manifest);
        if (intent.Target is not null && manifest.IsFile) Reject(session);
        await MessageAsync(session, context, delivery, (row, now) => { row.Status = "leased"; row.Attempts = lease.Attempt; row.LastAttemptAt = now; }, ct);
        await SupplierQueries.OrderAsync(session, context, intent.Order, (row, now) => { if (row.FulfillmentStatus == "ready") { row.FulfillmentStatus = "delivering"; row.Revision++; } }, ct);
        var result = new DeliveryDispatch(delivery, intent.Order, state.Generation!.Value, context.BotId, state.Telegram, intent.Target, manifest.IsFile, manifest.Payload.ToArray());
        await session.CommitAsync(ct);
        return result;
    }

    public async Task RecordOutcomeAsync(BrandContext context, JobLease lease, DeliveryOutcome outcome, CancellationToken ct = default)
    {
        Guid delivery = Origin(context, lease);
        if (!Enum.IsDefined(outcome.Kind)) throw new ArgumentException("Invalid delivery outcome.");
        await using var session = await sessions.BeginAsync(ct);
        Intent intent = await ReadIntentAsync(session, delivery, ct);
        State state = await LockAsync(session, context, intent.Order, ct);
        intent = await ReadIntentAsync(session, delivery, ct);
        if (intent.Status == "succeeded") { await session.CommitAsync(ct); return; }
        if ((outcome.Kind != DeliveryOutcomeKind.Succeeded && intent.Status != "leased") || intent.Attempt != lease.Attempt || intent.Generation != state.Generation || !state.Released || state.Held || state.Review) Reject(session);
        bool success = outcome.Kind == DeliveryOutcomeKind.Succeeded && outcome.MessageId is > 0 && (intent.Target is null || intent.Target == outcome.MessageId);
        bool retry = intent.Target is not null && outcome.Kind is DeliveryOutcomeKind.Unknown or DeliveryOutcomeKind.Retry;
        string status = success ? "succeeded" : retry ? "retry" : outcome.Kind == DeliveryOutcomeKind.Unknown ? "uncertain" : "failed";
        int fenced = await JobsAsync(session, context, intent.Order, row => row.Id == lease.Id && row.TelegramDeliveryId == delivery && row.Attempts == lease.Attempt && (row.Status == "leased" || row.Status == "retry" || row.Status == "failed"), (row, now) => { row.Status = success ? "succeeded" : retry ? "retry" : "failed"; row.LockedBy = null; row.LeaseUntil = null; row.AvailableAt = now.AddSeconds(5); row.CompletedAt = retry ? null : now; row.LastErrorCode = success ? "known_success" : status == "uncertain" ? "remote_outcome_unknown" : "credential_recovery"; }, ct);
        if (fenced != 1) Reject(session);
        await MessageAsync(session, context, delivery, (row, now) => { row.Status = status; row.TelegramMessageId = success ? outcome.MessageId : null; row.SentAt = success ? now : null; row.LastErrorCode = success ? "known_success" : "credential_recovery"; row.NextAttemptAt = now.AddSeconds(5); }, ct);
        if (success)
        {
            await PartsAsync(session, context, intent.Order, row => row.Status == "ready" && row.ManifestGeneration == intent.Generation, (row, now) => { row.Status = "released"; row.CompletedAt = now; }, ct);
            await PaymentSettlementStore.ChangeOrderAsync(session, intent.Order, (row, now) => row.FulfillmentStatus = "completed", ct);
        }
        await session.CommitAsync(ct);
    }

    public async Task RecoverExpiredAsync(BrandContext context, Guid delivery, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginAsync(ct);
        Intent intent = await ReadIntentAsync(session, delivery, ct);
        State state = await LockAsync(session, context, intent.Order, ct);
        if (!state.Released || state.Generation != intent.Generation || state.Held || state.Review) Reject(session);
        int changed = await JobsAsync(session, context, intent.Order, row => row.TelegramDeliveryId == delivery && row.Status == "leased" && row.LeaseUntil <= Platform.Persistence.Configurations.EfValues.UtcNow(), (row, now) => { row.Status = intent.Target is null ? "failed" : "retry"; row.LockedBy = null; row.LeaseUntil = null; row.AvailableAt = now; row.CompletedAt = intent.Target is null ? now : null; row.LastErrorCode = "remote_outcome_unknown"; }, ct);
        if (changed == 1) await MessageAsync(session, context, delivery, (row, now) => { if (row.Status == "leased") { row.Status = intent.Target is null ? "uncertain" : "retry"; row.LastErrorCode = "remote_outcome_unknown"; } }, ct);
        await session.CommitAsync(ct);
    }

    public async Task<Guid> AuthorizeSendRecoveryAsync(BrandContext context, AuthenticatedAdmin actor, Guid order, Guid command, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginAsync(ct);
        var audit = new AuditCommand(command, AuditAction.DeliveryRecovery, AuditEntity.Order, order, context.BrandId, AuditReason.Reconciliation,
            Guid.NewGuid(), SHA256.HashData(Encoding.ASCII.GetBytes("credential-send:" + order.ToString("D"))));
        bool fresh = await AdminAuditStore.AppendAsync(session, actor, audit, ct);
        State state = await LockAsync(session, context, order, ct);
        if (!state.Released || state.Held || state.Review) Reject(session);
        Guid? existing = await LatestAsync(session, order, ct);
        Guid result;
        if (existing is Guid id)
        {
            Intent intent = await ReadIntentAsync(session, id, ct);
            if (!fresh || intent.Status == "succeeded") { await session.CommitAsync(ct); return id; }
            if (intent.Status is not ("uncertain" or "failed")) Reject(session);
            if (intent.Target is not null)
            {
                result = await EnqueueAsync(session, context, state, order, null, command, ct, true);
                await session.CommitAsync(ct);
                return result;
            }
            await MessageAsync(session, context, id, (row, now) => { row.Status = "retry"; row.AuditEventId = command; row.NextAttemptAt = now; }, ct);
            await JobsAsync(session, context, order, row => row.TelegramDeliveryId == id && row.Status == "failed", (row, now) => { row.Status = "retry"; row.CompletedAt = null; row.LockedBy = null; row.LeaseUntil = null; row.AvailableAt = now; }, ct);
            result = id;
        }
        else result = await EnqueueAsync(session, context, state, order, null, command, ct);
        await session.CommitAsync(ct);
        return result;
    }

    public async Task<byte[]> ReadManifestAsync(BrandContext context, long actor, Guid order, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginAsync(ct);
        BrandUser member = await BrandUserStore.RequireAsync(session, context, actor, null, ct);
        State state = await LockAsync(session, context, order, ct);
        if (member.Id != state.Member || !state.Released || state.Held || state.Review) Reject(session);
        using Manifest manifest = await BuildAsync(session, context, order, state.Quantity, true, ct);
        Verify(state, order, manifest);
        return manifest.Bytes.ToArray();
    }

    private static Guid Origin(BrandContext context, JobLease lease) => lease.Intent.Kind == JobKind.SendTelegram && lease.Intent.BrandId == context.BrandId && lease.Intent.OriginId is Guid id ? id : throw new UnauthorizedAccessException();
    private static string Asset(Guid order, Guid generation) => "manifest:" + order.ToString("D") + ":" + generation.ToString("D");
    private static void Verify(State state, Guid order, Manifest manifest)
    {
        if (state.Status is not ("ready" or "delivering" or "completed") || !state.Released || state.Generation is not Guid generation || !manifest.Complete || state.Asset != Asset(order, generation) || state.Digest is null ||
            !CryptographicOperations.FixedTimeEquals(state.Digest, SHA256.HashData(manifest.Bytes))) throw new UnauthorizedAccessException("Manifest is unavailable.");
    }
    private static async Task<Guid> EnqueueAsync(DbSession session, BrandContext context, State state, Guid order, long? target, Guid? audit, CancellationToken ct, bool recovery = false)
    {
        Guid? prior = await LatestAsync(session, order, ct);
        if (prior is Guid old && !recovery) return old;
        int part = await session.RunEfStepAsync(false, async (data, token) => (await data.Data.Set<Entities.TelegramDelivery>().Where(row => row.OrderId == order && row.Purpose == "order_credentials").MaxAsync(row => (int?)row.PartNo, token) ?? 0) + 1, ct);
        Guid id = Guid.CreateVersion7();
        await session.RunEfStepAsync(true, async (data, token) => { await data.RequireOrderCommandAsync(context, order, token); data.Data.Add(new Entities.TelegramDelivery { Id = id, BrandId = context.BrandId, RecipientKind = "customer_dm", UserId = state.Member, Purpose = "order_credentials", Action = target is null ? "send" : "edit", TargetMessageId = target, DeliveryGeneration = state.Generation, DedupeKey = "credentials:" + order.ToString("D") + ":" + state.Generation!.Value.ToString("D") + (recovery ? ":" + audit!.Value.ToString("D") : ""), OrderId = order, AuditEventId = audit, PartNo = part }); return true; }, ct);
        await OutboxStore.EnqueueAsync(session, JobIntent.Telegram(context.BrandId, id), id, DateTime.UtcNow, ct);
        return id;
    }
    private static Task<Guid?> LatestAsync(DbSession session, Guid order, CancellationToken ct) => session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.TelegramDelivery>().Where(row => row.OrderId == order && row.Purpose == "order_credentials").OrderByDescending(row => row.PartNo).Select(row => (Guid?)row.Id).FirstOrDefaultAsync(token), ct);
    private static Task<Intent> ReadIntentAsync(DbSession session, Guid id, CancellationToken ct) => session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.TelegramDelivery>().Where(row => row.Id == id && row.Purpose == "order_credentials").Select(row => new Intent(row.OrderId!.Value, row.DeliveryGeneration!.Value, row.TargetMessageId, row.Status, row.Attempts)).SingleAsync(token), ct);
    private static async Task<State> LockAsync(DbSession session, BrandContext context, Guid order, CancellationToken ct)
    {
        Identity? identity = await session.RunEfStepAsync(false, (data, token) => (from o in data.Data.Set<Entities.Order>() join u in data.Data.Set<Entities.User>() on o.UserId equals u.Id where o.Id == order && u.BrandId == context.BrandId select new Identity(u.Id, o.ProductId!.Value)).SingleOrDefaultAsync(token), ct);
        if (identity is null) Reject(session);
        await session.RunEfStepAsync(false, async (data, token) => { await data.LockUserAsync(identity.User, context.BrandId, token); return await data.LockProductAsync(identity.Product, token); }, ct);
        await session.AdmitWriterAsync(context, ct);
        return await session.RunEfStepAsync(false, async (data, token) =>
        {
            await data.LockBrandAsync(context.BrandId, token);
            if (!await data.Data.Set<Entities.Brand>().AnyAsync(row => row.Id == context.BrandId && row.BotId == context.BotId && row.Active, token)) Reject(session);
            var coupons = await data.Data.Set<Entities.DiscountUse>().Where(row => row.OrderId == order).OrderBy(row => row.DiscountId).Select(row => row.DiscountId).Take(1001).ToListAsync(token);
            if (coupons.Count > 1000) data.RejectScope("Order coupon bound exceeded.");
            foreach (Guid coupon in coupons) await data.LockDiscountAsync(coupon, context.BrandId, token);
            await data.LockOrderAsync(order, token);
            return await (from o in data.Data.Set<Entities.Order>() join u in data.Data.Set<Entities.User>() on o.UserId equals u.Id where o.Id == order select new State(o.UserId, o.Quantity, o.OrderKind, o.PaymentStatus, o.FulfillmentStatus, o.DeliveryGeneration, o.ManifestAssetRef, o.ManifestDigest, o.ReleaseAuthorizedAt != null, o.HoldAt != null, o.ReviewCode != null, u.TelegramId, u.BlockedAt != null || u.BannedAt != null)).SingleAsync(token);
        }, ct);
    }
    private async Task<Manifest> BuildAsync(DbSession session, BrandContext context, Guid order, int quantity, bool frozen, CancellationToken ct)
    {
        using var bytes = new MemoryStream();
        var text = new StringBuilder();
        bool file = false;
        var cipher = new BrandCipher(secrets);
        int count = 0;
        try
        {
            using var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true);
            writer.Write(1); writer.Write(quantity);
            while (count < quantity)
            {
                var rows = await session.RunEfStepAsync(false, async (data, token) => { await data.LockOrderPartsAsync(order, count, token); return await data.Data.Set<Entities.Fulfillment>().Where(row => row.OrderId == order).OrderBy(row => row.UnitNo).Skip(count).Take(1000).Select(row => new Part(row.Id, row.UnitNo, row.ContentKind, row.DeliveredContent, row.DeliveredContentKeyId, row.Status, row.ManifestGeneration)).ToListAsync(token); }, ct);
                if (rows.Count == 0) break;
                foreach (Part part in rows)
                {
                    if (part.Number != count + 1 || part.Content is null || part.Key is null || part.Kind is null || (frozen ? part.Status is not ("ready" or "released") || part.Generation is null : part.Status != "acquired"))
                        return new([], [], false, true);
                    byte[] plain = cipher.Decrypt(context, "fulfillments", part.Id, "delivered_content", part.Key, part.Content);
                    try
                    {
                        writer.Write(part.Number); writer.Write(part.Kind); writer.Write(plain.Length); writer.Write(plain);
                        if (bytes.Length > 16777216) throw new ManifestNeedsRecoveryException();
                        if (part.Kind == "file") file = true;
                        if (!file)
                        {
                            try { text.Append(part.Number).Append(". ").Append(new UTF8Encoding(false, true).GetString(plain)).Append('\n'); }
                            catch (DecoderFallbackException) { file = true; }
                            if (text.Length > 4096) file = true;
                        }
                    }
                    finally { CryptographicOperations.ZeroMemory(plain); }
                    count++;
                }
            }
            byte[] content = bytes.ToArray();
            return new(content, file ? content.ToArray() : Encoding.UTF8.GetBytes(text.ToString()), count == quantity, file);
        }
        finally { CryptographicOperations.ZeroMemory(bytes.GetBuffer()); }
    }
    private static Task<bool> MessageAsync(DbSession session, BrandContext context, Guid id, Action<Entities.TelegramDelivery, DateTime> change, CancellationToken ct) => session.RunEfStepAsync(true, async (data, token) => { var row = await data.Data.Set<Entities.TelegramDelivery>().SingleAsync(row => row.Id == id, token); await data.RequireOrderCommandAsync(context, row.OrderId!.Value, token); await data.LockMessageAsync(id, token); change(row, await data.BrandClockAsync(context.BrandId, token)); return true; }, ct);
    private static Task<int> JobsAsync(DbSession session, BrandContext context, Guid order, System.Linq.Expressions.Expression<Func<Entities.Job, bool>> predicate, Action<Entities.Job, DateTime> change, CancellationToken ct) => session.RunEfStepAsync(true, async (data, token) => { await data.RequireOrderCommandAsync(context, order, token); await data.LockDeliveryJobsAsync(predicate, token); var rows = await data.Data.Set<Entities.Job>().Where(predicate).Take(1001).ToListAsync(token); if (rows.Count > 1000) data.RejectScope("Delivery job bound exceeded."); DateTime now = await data.BrandClockAsync(context.BrandId, token); foreach (var batch in rows.Chunk(128)) { foreach (var row in batch) change(row, now); await data.FlushAsync(token, false); } return rows.Count; }, ct);
    private static async Task PartsAsync(DbSession session, BrandContext context, Guid order, System.Linq.Expressions.Expression<Func<Entities.Fulfillment, bool>> predicate, Action<Entities.Fulfillment, DateTime> change, CancellationToken ct)
    {
        int offset = 0;
        while (true)
        {
            int count = await session.RunEfStepAsync(true, async (data, token) => { await data.RequireOrderCommandAsync(context, order, token); await data.LockOrderPartsAsync(order, offset, token); var rows = await data.Data.Set<Entities.Fulfillment>().Where(row => row.OrderId == order).OrderBy(row => row.UnitNo).Skip(offset).Take(128).ToListAsync(token); DateTime now = await data.BrandClockAsync(context.BrandId, token); foreach (var row in rows.Where(predicate.Compile())) change(row, now); return rows.Count; }, ct);
            if (count < 128) return; offset += count;
        }
    }
    private sealed class Manifest(byte[] bytes, byte[] payload, bool complete, bool file) : IDisposable
    {
        public byte[] Bytes { get; } = bytes;
        public byte[] Payload { get; } = payload;
        public bool Complete { get; } = complete;
        public bool IsFile { get; } = file;
        public void Dispose() { CryptographicOperations.ZeroMemory(Bytes); CryptographicOperations.ZeroMemory(Payload); }
    }
    private sealed class ManifestNeedsRecoveryException : Exception { }
    private sealed record Identity(Guid User, Guid Product);
    private sealed record State(Guid Member, int Quantity, string Kind, string Payment, string Status, Guid? Generation, string? Asset, byte[]? Digest, bool Released, bool Held, bool Review, long Telegram, bool Blocked);
    private sealed record Intent(Guid Order, Guid Generation, long? Target, string Status, long Attempt);
    private sealed record Part(Guid Id, int Number, string? Kind, byte[]? Content, string? Key, string Status, Guid? Generation);
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Reject(DbSession session) { session.RejectApplication(); throw new UnauthorizedAccessException("Credential delivery requires current complete-order authority."); }
}
