using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Platform.Security;

namespace Platform.Persistence;

public sealed record SqliteCouponDispatch(long ChatId, string Text);

public sealed class SqliteCouponDeliveryStore(SqliteSessionFactory sessions, SecretSnapshot secrets)
{
    public Task<JobLease?> ClaimNoticeAsync(BrandContext context, Guid worker, CancellationToken ct = default) => sessions.RunAsync(true, (_, tx, token) => OutboxStore.ClaimSqliteAsync(tx, context.BrandId, JobKind.CouponNotice, worker, TimeSpan.FromSeconds(90), token), ct);
    public Task PrepareAsync(BrandContext context, JobLease lease, CancellationToken ct = default)
    {
        if (lease.Intent.Kind != JobKind.CouponNotice || lease.Intent.BrandId != context.BrandId || lease.Intent.OriginId is not Guid coupon || coupon == Guid.Empty) throw new UnauthorizedAccessException();
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            Guid delivery = await SqliteSessionFactory.StepEfAsync(tx, true, async (data, cancel) =>
            {
                Guid? member = await Eligible(data, context).Where(value => value.Id == coupon).Select(value => value.EligibleUserId).SingleOrDefaultAsync(cancel);
                if (member is null) data.RejectScope("The original paid prepaid entitlement is unavailable.");
                await data.RequireWalletCommandAsync(member.Value, cancel);
                string key = "prepaid-notice:" + coupon.ToString("N");
                var prior = await data.Data.Set<Entities.TelegramDelivery>().SingleOrDefaultAsync(value => value.DedupeKey == key, cancel);
                if (prior is not null)
                {
                    if (prior.BrandId != context.BrandId || prior.DiscountId != coupon || prior.UserId != member || prior.Purpose != "discount_notice" || prior.Action != "send") data.RejectScope("Coupon notice identity conflict.");
                    return prior.Id;
                }
                Guid id = Guid.CreateVersion7(); data.Data.Add(new Entities.TelegramDelivery { Id = id, BrandId = context.BrandId, RecipientKind = "customer_dm", UserId = member, Purpose = "discount_notice", Action = "send", DedupeKey = key, DiscountId = coupon }); return id;
            }, token);
            await OutboxStore.EnqueueSqliteAsync(tx, JobIntent.Telegram(context.BrandId, delivery), delivery, DateTime.UtcNow, token);
            await OutboxStore.CompleteSqliteAsync(tx, lease, token); return 0;
        }, ct);
    }
    public Task<SqliteCouponDispatch?> BeginAsync(BrandContext context, JobLease lease, CancellationToken ct = default)
    {
        Guid delivery = Require(context, lease);
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            var state = await SqliteSessionFactory.StepEfAsync(tx, false, (data, cancel) =>
                (from value in data.Data.Set<Entities.TelegramDelivery>()
                 join coupon in Eligible(data, context) on value.DiscountId equals coupon.Id
                 join user in data.Data.Set<Entities.User>() on value.UserId equals user.Id
                 where value.Id == delivery && value.BrandId == context.BrandId && value.UserId == coupon.EligibleUserId && user.BrandId == context.BrandId && value.RecipientKind == "customer_dm" && value.Purpose == "discount_notice" && value.Action == "send" && value.TargetMessageId == null
                 select new { value.Status, Member = user.Id, Actor = user.TelegramId, Coupon = coupon.Id, coupon.CodeCiphertext, coupon.CodeKeyId }).SingleOrDefaultAsync(cancel), token);
            if (state is null) throw new UnauthorizedAccessException("Prepaid delivery is unavailable.");
            JobLease renewed = await OutboxStore.RenewSqliteAsync(tx, lease, TimeSpan.FromSeconds(90), token);
            if (state.Status == "succeeded") { await OutboxStore.CompleteSqliteAsync(tx, renewed, token); return (SqliteCouponDispatch?)null; }
            if (state.Status is not ("pending" or "retry")) throw new UnauthorizedAccessException("Prepaid delivery is held for review.");
            byte[] plain = new BrandCipher(secrets).Decrypt(context, "discounts", state.Coupon, "code_ciphertext", state.CodeKeyId, state.CodeCiphertext); string code;
            try { code = new UTF8Encoding(false, true).GetString(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            if (code.Length != 48 || !code.All(char.IsAsciiHexDigit)) throw new UnauthorizedAccessException("Prepaid entitlement code is invalid.");
            await SqliteSessionFactory.StepEfAsync(tx, true, async (data, cancel) =>
            {
                await data.RequireWalletCommandAsync(state.Member, cancel);
                var row = await data.Data.Set<Entities.TelegramDelivery>().SingleOrDefaultAsync(value => value.Id == delivery && (value.Status == "pending" || value.Status == "retry"), cancel);
                if (row is null) data.RejectScope("Prepaid delivery state was superseded.");
                row.Status = "leased"; row.Attempts = renewed.Attempt; row.LastAttemptAt = await data.BrandClockAsync(context.BrandId, cancel); return true;
            }, token);
            return new SqliteCouponDispatch(state.Actor, "Akanti staging prepaid entitlement\nCode: " + code);
        }, ct);
    }
    public Task RecordOutcomeAsync(BrandContext context, JobLease lease, DeliveryOutcome result, CancellationToken ct = default)
    {
        Guid delivery = Require(context, lease);
        return sessions.RunEfAsync(true, null, async (data, token) =>
        {
            var row = await RequireDeliveryAsync(data, context, delivery, token);
            DateTime now = await data.BrandClockAsync(context.BrandId, token);
            string worker = lease.Worker.ToString("D");
            var job = await data.Data.Set<Entities.Job>().SingleOrDefaultAsync(value => value.Id == lease.Id && value.BrandId == context.BrandId && value.TelegramDeliveryId == delivery && value.Status == "leased" && value.Attempts == lease.Attempt && value.LockedBy == worker && value.LeaseUntil > now, token);
            if (job is null || row.Status != "leased" || row.Attempts != lease.Attempt) data.RejectScope("Prepaid delivery lease expired or state was superseded.");
            bool success = result.Kind == DeliveryOutcomeKind.Succeeded && result.MessageId is > 0;
            string error = success ? "known_success" : "remote_outcome_unknown";
            job.Status = success ? "succeeded" : "failed"; job.LockedBy = null; job.LeaseUntil = null; job.CompletedAt = now; job.LastErrorCode = error;
            row.Status = success ? "succeeded" : "uncertain"; row.TelegramMessageId = success ? result.MessageId : null; row.SentAt = success ? now : null; row.LastErrorCode = error; return true;
        }, ct);
    }
    public Task<int> RecoverExpiredAsync(BrandContext context, CancellationToken ct = default) => sessions.RunEfAsync(true, null, async (data, token) =>
    {
        DateTime now = await data.BrandClockAsync(context.BrandId, token);
        var ids = await (from delivery in data.Data.Set<Entities.TelegramDelivery>() join job in data.Data.Set<Entities.Job>() on delivery.Id equals job.TelegramDeliveryId where delivery.BrandId == context.BrandId && delivery.Purpose == "discount_notice" && delivery.Status == "leased" && job.Status == "leased" && job.LeaseUntil <= now select delivery.Id).Take(50).ToArrayAsync(token);
        foreach (Guid id in ids)
        {
            var delivery = await RequireDeliveryAsync(data, context, id, token);
            var jobs = await data.Data.Set<Entities.Job>().Where(value => value.TelegramDeliveryId == id && value.Status == "leased" && value.LeaseUntil <= now).Take(2).ToArrayAsync(token);
            if (jobs.Length == 0) continue;
            if (jobs.Length != 1 || delivery.Status != "leased") data.RejectScope("Prepaid delivery recovery was superseded.");
            var job = jobs[0]; job.Status = "failed"; job.LockedBy = null; job.LeaseUntil = null; job.CompletedAt = now; job.LastErrorCode = "remote_outcome_unknown";
            delivery.Status = "uncertain"; delivery.LastErrorCode = "remote_outcome_unknown";
            await data.FlushAsync(token, false);
        }
        return ids.Length;
    }, ct);
    private static IQueryable<Entities.Discount> Eligible(EfSession data, BrandContext context) =>
        from coupon in data.Data.Set<Entities.Discount>()
        join order in data.Data.Set<Entities.Order>() on coupon.SourceOrderId equals order.Id
        join user in data.Data.Set<Entities.User>() on order.UserId equals user.Id
        join brand in data.Data.Set<Entities.Brand>() on coupon.BrandId equals brand.Id
        where coupon.BrandId == context.BrandId && brand.BotId == context.BotId && brand.Active && coupon.Kind == "prepaid_entitlement" && coupon.EligibleUserId == user.Id && user.BrandId == coupon.BrandId && order.OrderKind == "prepaid_units" && order.PaymentStatus == "paid" && order.FulfillmentStatus == "completed" && user.BlockedAt == null && user.BannedAt == null
        select coupon;
    private static async Task<Entities.TelegramDelivery> RequireDeliveryAsync(EfSession data, BrandContext context, Guid delivery, CancellationToken ct)
    {
        if (!await data.Data.Set<Entities.Brand>().AnyAsync(value => value.Id == context.BrandId && value.BotId == context.BotId, ct)) data.RejectScope("Prepaid delivery brand is unavailable.");
        var row = await data.Data.Set<Entities.TelegramDelivery>().SingleOrDefaultAsync(value => value.Id == delivery && value.BrandId == context.BrandId && value.Purpose == "discount_notice" && value.UserId != null, ct);
        if (row is null) data.RejectScope("Prepaid delivery is unavailable.");
        await data.RequireWalletCommandAsync(row.UserId!.Value, ct); return row;
    }
    private static Guid Require(BrandContext context, JobLease lease) => lease.Intent.Kind == JobKind.SendTelegram && lease.Intent.BrandId == context.BrandId && lease.Intent.OriginId is Guid id && id != Guid.Empty ? id : throw new UnauthorizedAccessException("Prepaid Telegram job scope is unavailable.");
}
