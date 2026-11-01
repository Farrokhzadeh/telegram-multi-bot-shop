using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using NpgsqlTypes;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

public enum CouponKind { Promotion, Goodwill, Prepaid }
public enum CouponLookupOutcome { Found, NotFound, RateLimited }
public sealed record CouponLookup(CouponLookupOutcome Outcome, Guid? DiscountId = null);
public sealed record CouponTerms(Guid Id, string Code, CouponKind Kind, decimal Percent, Guid? ProductId = null, Guid? MemberId = null,
    Guid? SourceOrderId = null, long? MaximumUses = null, long? MaximumPerMember = null, DateTime? StartsAt = null, DateTime? EndsAt = null, Guid? BatchId = null)
{
    public override string ToString() => "CouponTerms [redacted]";
}
public sealed record CouponClaim(Guid DiscountId, Guid OrderId, Guid RevisionId, int Units, Money Saved, string Status);

public sealed class PricingStore(DbSessionFactory sessions, SecretSnapshot secrets)
{
    public async Task<Guid> IssueAsync(DbSession session, BrandContext context, AuthenticatedAdmin actor, CouponTerms terms, Guid commandId, CancellationToken ct = default)
    {
        try { return await IssueCoreAsync(session, context, actor, terms, commandId, ct); }
        catch { Poison(session); throw; }
    }

    private async Task<Guid> IssueCoreAsync(DbSession session, BrandContext context, AuthenticatedAdmin actor, CouponTerms terms, Guid commandId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(terms);
        string code = Normalize(terms.Code);
        if (terms.Id == Guid.Empty || terms.ProductId == Guid.Empty || terms.MemberId == Guid.Empty || terms.SourceOrderId == Guid.Empty || terms.BatchId == Guid.Empty ||
            !Enum.IsDefined(terms.Kind) || terms.Percent is < 0 or > 100 || decimal.Round(terms.Percent, 2) != terms.Percent || terms.MaximumUses < 0 || terms.MaximumPerMember < 0 ||
            terms.StartsAt?.Kind is DateTimeKind.Local or DateTimeKind.Unspecified || terms.EndsAt?.Kind is DateTimeKind.Local or DateTimeKind.Unspecified || terms.EndsAt <= terms.StartsAt ||
            (terms.Kind == CouponKind.Prepaid ? terms.ProductId is null || terms.MemberId is null || terms.SourceOrderId is null || terms.Percent != 100 || terms.MaximumUses is not > 0 || terms.BatchId is not null : terms.SourceOrderId is not null))
            throw new PricingRejectedException();
        await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
        string key = await session.QuerySingleAsync(new DbStatement("SELECT d.code_key_id FROM (SELECT 1) seed LEFT JOIN shop.discounts d ON d.id=$1 AND d.brand_id=$2", DbValue.Uuid(terms.Id), DbValue.Uuid(context.BrandId)),
            row => row.IsDBNull(0) ? secrets.CurrentBrandKeyId(context) : row.GetString(0), ct);
        byte[] digest = Digest(context, key, code);
        var command = new AuditCommand(commandId, AuditAction.CouponIssue, AuditEntity.Discount, terms.Id, context.BrandId, AuditReason.OperatorReview, Guid.NewGuid(), Commitment(terms, digest), newRevision: 0);
        if (!await AdminAuditStore.AppendAsync(session, actor, command, ct)) return terms.Id;
        if (terms.MemberId is Guid member) await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM (SELECT id FROM shop.users WHERE id=$1 AND brand_id=$2 FOR UPDATE) u", DbValue.Uuid(member), DbValue.Uuid(context.BrandId)), row => row.GetInt64(0), ct);
        if (terms.ProductId is Guid product) await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM (SELECT id FROM shop.products WHERE id=$1 FOR UPDATE) p", DbValue.Uuid(product)), row => row.GetInt64(0), ct);
        await LockBrandAsync(session, context, ct);
        byte[] plain = Encoding.UTF8.GetBytes(code);
        byte[] encrypted;
        try { encrypted = new BrandCipher(secrets).Encrypt(context, "discounts", terms.Id, "code_ciphertext", plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        await session.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.discounts(id,brand_id,code_digest,code_ciphertext,code_key_id,display_prefix,issuance_batch_id,kind,percent_off,product_id,
                eligible_user_id,source_order_id,max_total_uses,max_uses_per_user,starts_at,ends_at,created_by_admin_id)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17)
            """, DbValue.Uuid(terms.Id), DbValue.Uuid(context.BrandId), DbValue.Binary(digest), DbValue.Binary(encrypted), DbValue.Text(key), DbValue.Text(code[..Math.Min(3, code.Length)]),
            DbValue.Uuid(terms.BatchId ?? terms.Id), DbValue.Text(terms.Kind switch { CouponKind.Promotion => "promotion", CouponKind.Goodwill => "goodwill", CouponKind.Prepaid => "prepaid_entitlement", _ => throw new PricingRejectedException() }),
            DbValue.Numeric(terms.Percent), Uuid(terms.ProductId), Uuid(terms.MemberId), Uuid(terms.SourceOrderId), Number(terms.MaximumUses), Number(terms.MaximumPerMember), Time(terms.StartsAt), Time(terms.EndsAt), DbValue.Uuid(actor.AdminId)), ct);
        await session.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.discount_revisions(id,discount_id,revision_no,audit_event_id,percent_off,max_total_uses,max_uses_per_user,usage_adjustment_units,
                redeemed_count_at_revision,held_units_at_revision,starts_at,ends_at,active,product_id,eligible_user_id)
            VALUES (gen_random_uuid(),$1,0,$2,$3,$4,$5,0,0,0,$6,$7,true,$8,$9)
            """, DbValue.Uuid(terms.Id), DbValue.Uuid(commandId), DbValue.Numeric(terms.Percent), Number(terms.MaximumUses), Number(terms.MaximumPerMember), Time(terms.StartsAt), Time(terms.EndsAt), Uuid(terms.ProductId), Uuid(terms.MemberId)), ct);
        if (terms.MemberId is not null) await OutboxStore.EnqueueAsync(session, JobIntent.Coupon(context.BrandId, terms.Id), terms.Id, DateTime.UtcNow, ct);
        return terms.Id;
    }

    public async Task<CouponLookup> LookupAsync(BrandContext context, long verifiedActorId, string suppliedCode, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginAsync(ct);
        BrandUser member = await RequireCustomerAsync(session, context, verifiedActorId, ct);
        long attempts = await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.bot_activity_events WHERE user_id=$1 AND idempotency_key LIKE 'coupon_lookup:%' AND occurred_at>clock_timestamp()-INTERVAL '1 minute'", DbValue.Uuid(member.Id)), row => row.GetInt64(0), ct);
        if (attempts >= 20) return new(CouponLookupOutcome.RateLimited);
        Guid? found = null;
        string? code = null;
        try { code = Normalize(suppliedCode); } catch (PricingRejectedException) { }
        if (code is not null)
        {
            var keys = await session.QueryManyAsync(new DbStatement("SELECT DISTINCT code_key_id FROM shop.discounts WHERE brand_id=$1 AND active ORDER BY code_key_id", DbValue.Uuid(context.BrandId)), row => row.GetString(0), 32, ct);
            foreach (string key in keys)
            {
                byte[] digest = Digest(context, key, code);
                found = await session.QuerySingleAsync(new DbStatement("""
                    SELECT d.id FROM (SELECT 1) seed LEFT JOIN shop.discounts d ON d.brand_id=$1 AND d.code_key_id=$2 AND d.code_digest=$3 AND d.active
                        AND (d.eligible_user_id IS NULL OR d.eligible_user_id=$4) AND (d.starts_at IS NULL OR d.starts_at<=clock_timestamp()) AND (d.ends_at IS NULL OR d.ends_at>clock_timestamp())
                        AND (d.kind<>'prepaid_entitlement' OR EXISTS(SELECT 1 FROM shop.orders s WHERE s.id=d.source_order_id AND s.user_id=$4 AND s.product_id=d.product_id AND s.order_kind='prepaid_units' AND s.payment_status='paid'))
                    """, DbValue.Uuid(context.BrandId), DbValue.Text(key), DbValue.Binary(digest), DbValue.Uuid(member.Id)), row => row.IsDBNull(0) ? (Guid?)null : row.GetGuid(0), ct);
                if (found is not null) break;
            }
        }
        await session.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.bot_activity_events(id,user_id,event_type,source_kind,action_code,outcome_code,idempotency_key)
            VALUES (gen_random_uuid(),$1,'discount','flow','discounts_open',$2,$3)
            """, DbValue.Uuid(member.Id), DbValue.Text(found is null ? "rejected" : "accepted"), DbValue.Text("coupon_lookup:" + Guid.NewGuid().ToString("N"))), ct);
        await session.CommitAsync(ct);
        return new(found is null ? CouponLookupOutcome.NotFound : CouponLookupOutcome.Found, found);
    }

    public async Task<string> ReadCodeAsync(DbSession session, BrandContext context, long verifiedActorId, Guid discountId, CancellationToken ct = default)
    {
        BrandUser member = await RequireCustomerAsync(session, context, verifiedActorId, ct);
        var value = await session.QuerySingleAsync(new DbStatement("""
            SELECT d.code_ciphertext,d.code_key_id FROM (SELECT 1) seed LEFT JOIN shop.discounts d ON d.id=$1 AND d.brand_id=$2 AND (d.eligible_user_id IS NULL OR d.eligible_user_id=$3)
                AND (d.kind<>'prepaid_entitlement' OR EXISTS(SELECT 1 FROM shop.orders s WHERE s.id=d.source_order_id AND s.user_id=$3 AND s.product_id=d.product_id AND s.order_kind='prepaid_units' AND s.payment_status='paid'))
            """, DbValue.Uuid(discountId), DbValue.Uuid(context.BrandId), DbValue.Uuid(member.Id)), row => row.IsDBNull(0) ? null : new ProtectedCode(row.GetBinary(0), row.GetString(1)), ct);
        if (value is null) Reject(session);
        byte[] plain = new BrandCipher(secrets).Decrypt(context, "discounts", discountId, "code_ciphertext", value.Key, value.Content);
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public async Task<CouponClaim> HoldAsync(DbSession session, BrandContext context, long verifiedActorId, Guid discountId, Guid orderId, CancellationToken ct = default)
    {
        try { return await HoldCoreAsync(session, context, verifiedActorId, discountId, orderId, ct); }
        catch { Poison(session); throw; }
    }

    private static async Task<CouponClaim> HoldCoreAsync(DbSession session, BrandContext context, long verifiedActorId, Guid discountId, Guid orderId, CancellationToken ct)
    {
        BrandUser member = await RequireCustomerAsync(session, context, verifiedActorId, ct);
        await LockBrandAsync(session, context, ct);
        await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM (SELECT id FROM shop.discounts WHERE id=$1 AND brand_id=$2 FOR UPDATE) d", DbValue.Uuid(discountId), DbValue.Uuid(context.BrandId)), row => row.GetInt64(0), ct);
        var order = await session.QuerySingleAsync<ClaimOrder?>(new DbStatement("""
            SELECT o.id,o.quantity,o.currency,o.unit_price_usdt::text,o.unit_price_toman::text,o.auto_price_toman_snapshot,o.fx_rate_snapshot::text,
                o.fx_source_snapshot,o.fx_fetched_at_snapshot,o.discount_pct_snapshot::text,o.discount_units_snapshot,o.price_calculation_version,o.payment_status,o.pricing_rounding_step_snapshot::text
            FROM (SELECT 1) seed LEFT JOIN (SELECT * FROM shop.orders WHERE id=$1 AND user_id=$2 FOR UPDATE) o ON true
            """, DbValue.Uuid(orderId), DbValue.Uuid(member.Id)), row => row.IsDBNull(0) ? null : new ClaimOrder(row.GetInt32(1), row.GetString(2), ExactDecimal.Parse(row.GetString(3)),
                row.IsDBNull(4) ? null : ExactDecimal.Parse(row.GetString(4)), row.GetBoolean(5), row.IsDBNull(6) ? null : new FxRate(ExactDecimal.Parse(row.GetString(6)), row.GetString(7), row.GetUtc(8)),
                row.IsDBNull(9) ? null : ExactDecimal.Parse(row.GetString(9)), row.IsDBNull(10) ? null : row.GetInt32(10), row.GetString(11), row.GetString(12), ExactDecimal.Parse(row.GetString(13))), ct);
        if (order is null) Reject(session);
        CouponClaim? existing = await ReadClaimAsync(session, discountId, orderId, ct);
        if (existing is not null)
        {
            if (existing.Status == "released") Reject(session);
            return existing;
        }
        bool otherClaim = await session.QuerySingleAsync(new DbStatement("SELECT EXISTS(SELECT 1 FROM shop.discount_uses WHERE order_id=$1 AND discount_id<>$2)", DbValue.Uuid(orderId), DbValue.Uuid(discountId)), row => row.GetBoolean(0), ct);
        if (otherClaim) Reject(session);
        var grant = await session.QuerySingleAsync(new DbStatement("""
            SELECT r.id,d.percent_off::text FROM (SELECT 1) seed LEFT JOIN shop.discounts d ON d.id=$1 AND d.brand_id=$2 AND d.active AND (d.starts_at IS NULL OR d.starts_at<=clock_timestamp()) AND (d.ends_at IS NULL OR d.ends_at>clock_timestamp())
            LEFT JOIN shop.discount_revisions r ON r.discount_id=d.id AND r.revision_no=d.revision
            """, DbValue.Uuid(discountId), DbValue.Uuid(context.BrandId)), row => row.IsDBNull(0) ? null : new Grant(row.GetGuid(0), ExactDecimal.Parse(row.GetString(1))), ct);
        if (grant is null || order.Status is "expired" or "cancelled" || order.Percent != grant.Percent || order.Units != order.Quantity || order.Version != PricingPolicy.Version || order.Step != PricingPolicy.TomanStep) Reject(session);
        Currency currency = order.Currency == "TOMAN" ? Currency.Toman : Currency.Usd;
        Money original = currency == Currency.Toman ? PricingPolicy.TomanTotal(order.UnitUsdt, order.UnitToman, order.Quantity, 0, order.Automatic, order.Rate) : PricingPolicy.DiscountTotal(order.UnitUsdt, order.Quantity, 0, Currency.Usd);
        Money discounted = currency == Currency.Toman ? PricingPolicy.TomanTotal(order.UnitUsdt, order.UnitToman, order.Quantity, grant.Percent, order.Automatic, order.Rate) : PricingPolicy.DiscountTotal(order.UnitUsdt, order.Quantity, grant.Percent, Currency.Usd);
        Money saved = original.Subtract(discounted);
        await session.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.discount_uses(discount_id,order_id,discount_revision_id,units_covered,saved_amount,saved_currency,status)
            VALUES ($1,$2,$3,$4,$5,$6,'held')
            """, DbValue.Uuid(discountId), DbValue.Uuid(orderId), DbValue.Uuid(grant.Id), DbValue.Integer(order.Quantity), DbValue.Money(saved, currency), DbValue.Currency(currency)), ct);
        return new(discountId, orderId, grant.Id, order.Quantity, saved, "held");
    }

    public async Task<bool> TransitionClaimAsync(DbSession session, BrandContext context, Guid discountId, Guid orderId, bool redeem, CancellationToken ct = default)
    {
        try
        {
            await LockBrandAsync(session, context, ct);
            await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM (SELECT id FROM shop.discounts WHERE id=$1 AND brand_id=$2 FOR UPDATE) d", DbValue.Uuid(discountId), DbValue.Uuid(context.BrandId)), row => row.GetInt64(0), ct);
            var order = await session.QuerySingleAsync(new DbStatement("""
                SELECT o.id,o.payment_status,o.release_authorized_at IS NOT NULL,o.order_kind,o.fulfillment_status,o.resolution_kind
                FROM (SELECT 1) seed LEFT JOIN (SELECT o.* FROM shop.orders o JOIN shop.users u ON u.id=o.user_id WHERE o.id=$1 AND u.brand_id=$2 FOR UPDATE OF o) o ON true
                """, DbValue.Uuid(orderId), DbValue.Uuid(context.BrandId)), row => row.IsDBNull(0) ? null : new ClaimState(row.GetString(1), row.GetBoolean(2), row.GetString(3), row.GetString(4), row.IsDBNull(5) ? null : row.GetString(5)), ct);
            if (order is null) Reject(session);
            CouponClaim? claim = await ReadClaimAsync(session, discountId, orderId, ct);
            if (claim is null) Reject(session);
            string target = redeem ? "redeemed" : "released";
            if (claim.Status == target) return false;
            if (claim.Status != "held" || (redeem ? order.Payment is not ("paid" or "not_required") || !(order.Release || (order.Kind == "prepaid_units" && order.Fulfillment == "completed")) :
                order.Payment is not ("expired" or "cancelled") && order.Resolution is not ("local_preorder_cancellation" or "immediate_crypto_shortage_credit" or "supplier_wallet_credit"))) Reject(session);
            await session.ExecuteAsync(new DbStatement("UPDATE shop.discount_uses SET status=$1,transitioned_at=CURRENT_TIMESTAMP WHERE discount_id=$2 AND order_id=$3", DbValue.Text(target), DbValue.Uuid(discountId), DbValue.Uuid(orderId)), ct);
            if (redeem) await session.ExecuteAsync(new DbStatement("UPDATE shop.discounts SET redeemed_count=redeemed_count+$1 WHERE id=$2", DbValue.Bigint(claim.Units), DbValue.Uuid(discountId)), ct);
            return true;
        }
        catch { Poison(session); throw; }
    }

    public async Task<Guid?> ScheduleFxAsync(BrandContext context, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginAsync(ct);
        var state = await session.QuerySingleAsync(new DbStatement("SELECT fx_enabled,fx_fetched_at,clock_timestamp(),fx_source FROM shop.brands WHERE id=$1 AND bot_id=$2 AND active FOR SHARE", DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId)),
            row => (Enabled: row.GetBoolean(0), Fetched: row.IsDBNull(1) ? (DateTime?)null : row.GetUtc(1), Now: row.GetUtc(2), Source: row.IsDBNull(3) ? null : row.GetString(3)), ct);
        if (!state.Enabled || (state.Source == PricingPolicy.FxSource && state.Fetched > state.Now.AddHours(-1) && state.Fetched <= state.Now)) return null;
        byte[] digest = SHA256.HashData(Encoding.ASCII.GetBytes("brand-fx-v1:" + context.BrandId.ToString("D") + ":" + (state.Now.Ticks / TimeSpan.TicksPerHour).ToString(CultureInfo.InvariantCulture)));
        Guid id = await OutboxStore.EnqueueAsync(session, JobIntent.BrandFx(context.BrandId), new Guid(digest.AsSpan(0, 16)), state.Now, ct);
        await session.CommitAsync(ct);
        return id;
    }

    public async Task ApplyFxAsync(DbSession session, JobLease lease, long expectedPolicyRevision, ReadOnlyMemory<byte> response, CancellationToken ct = default)
    {
        try
        {
            decimal rate = PricingPolicy.ParseNobitex(response);
            Guid brand = FxBrand(lease);
            await OutboxStore.RenewAsync(session, lease, TimeSpan.FromSeconds(30), ct);
            int changed = await session.ExecuteAsync(new DbStatement("""
                UPDATE shop.brands SET current_fx=$1,fx_source=$2,fx_fetched_at=clock_timestamp(),fx_error_code=NULL,policy_version=policy_version+1,updated_at=clock_timestamp()
                WHERE id=$3 AND active AND fx_enabled AND policy_version=$4
                """, DbValue.Numeric(rate), DbValue.Text(PricingPolicy.FxSource), DbValue.Uuid(brand), DbValue.Bigint(expectedPolicyRevision)), ct);
            if (changed != 1) Reject(session);
            await OutboxStore.CompleteAsync(session, lease, ct);
        }
        catch { Poison(session); throw; }
    }

    public async Task FailFxAsync(DbSession session, JobLease lease, long expectedPolicyRevision, CancellationToken ct = default)
    {
        try
        {
            Guid brand = FxBrand(lease);
            await OutboxStore.RenewAsync(session, lease, TimeSpan.FromSeconds(30), ct);
            int changed = await session.ExecuteAsync(new DbStatement("UPDATE shop.brands SET fx_error_code='fx_unavailable' WHERE id=$1 AND active AND fx_enabled AND policy_version=$2", DbValue.Uuid(brand), DbValue.Bigint(expectedPolicyRevision)), ct);
            if (changed != 1) Reject(session);
            await OutboxStore.RetryAsync(session, lease, TimeSpan.FromMinutes(1), 5, JobError.Transient, ct);
        }
        catch { Poison(session); throw; }
    }

    private byte[] Digest(BrandContext context, string key, string code)
    {
        byte[] bytes = Encoding.UTF8.GetBytes("coupon-lookup-v1:" + context.BrandId.ToString("D") + ":" + code);
        try { return secrets.UseBrandKey(context, key, secret => HMACSHA256.HashData(secret, bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static byte[] Commitment(CouponTerms terms, byte[] digest)
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(digest); writer.Write((int)terms.Kind); writer.Write(ExactDecimal.Format(terms.Percent));
            writer.Write((terms.ProductId ?? Guid.Empty).ToByteArray()); writer.Write((terms.MemberId ?? Guid.Empty).ToByteArray()); writer.Write((terms.SourceOrderId ?? Guid.Empty).ToByteArray()); writer.Write((terms.BatchId ?? Guid.Empty).ToByteArray());
            writer.Write(terms.MaximumUses ?? -1); writer.Write(terms.MaximumPerMember ?? -1); writer.Write(terms.StartsAt?.Ticks ?? -1); writer.Write(terms.EndsAt?.Ticks ?? -1);
        }
        return SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)));
    }
    private static string Normalize(string code)
    {
        if (code is null) throw new PricingRejectedException();
        string normalized = code.Trim().ToUpperInvariant();
        if (normalized.Length is < 1 or > 64 || normalized.Any(char.IsControl)) throw new PricingRejectedException();
        return normalized;
    }
    private static async Task LockBrandAsync(DbSession session, BrandContext context, CancellationToken ct)
    {
        long count = await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM (SELECT id FROM shop.brands WHERE id=$1 AND bot_id=$2 FOR UPDATE) b", DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId)), row => row.GetInt64(0), ct);
        if (count != 1) Reject(session);
    }
    private static async Task<BrandUser> RequireCustomerAsync(DbSession session, BrandContext context, long actor, CancellationToken ct)
    {
        await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM (SELECT id FROM shop.users WHERE brand_id=$1 AND telegram_id=$2 FOR UPDATE) u", DbValue.Uuid(context.BrandId), DbValue.Bigint(actor)), row => row.GetInt64(0), ct);
        BrandUser member = await BrandUserStore.RequireAsync(session, context, actor, null, ct);
        if (member.Blocked || member.Banned) Reject(session);
        return member;
    }
    private static Task<CouponClaim?> ReadClaimAsync(DbSession session, Guid discount, Guid order, CancellationToken ct) =>
        session.QuerySingleAsync<CouponClaim?>(new DbStatement("SELECT c.discount_revision_id,c.units_covered,c.saved_amount::text,c.saved_currency,c.status FROM (SELECT 1) seed LEFT JOIN shop.discount_uses c ON c.discount_id=$1 AND c.order_id=$2", DbValue.Uuid(discount), DbValue.Uuid(order)),
            row => row.IsDBNull(0) ? null : new(discount, order, row.GetGuid(0), row.GetInt32(1), Money.Parse(row.GetString(2), CurrencyCodes.Parse(row.GetString(3))), row.GetString(4)), ct);
    private static Guid FxBrand(JobLease lease) => lease.Intent.Kind == JobKind.RefreshFx && lease.Intent.BrandId is Guid brand ? brand : throw new PricingRejectedException();
    private static DbValue Uuid(Guid? value) => value is Guid id ? DbValue.Uuid(id) : DbValue.Null(NpgsqlDbType.Uuid);
    private static DbValue Number(long? value) => value is long number ? DbValue.Bigint(number) : DbValue.Null(NpgsqlDbType.Bigint);
    private static DbValue Time(DateTime? value) => value is DateTime time ? DbValue.Utc(time) : DbValue.Null(NpgsqlDbType.TimestampTz);
    private static void Poison(DbSession session) { if (session.State == DbSessionState.Active) session.RejectApplication(); }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Reject(DbSession session) { Poison(session); throw new UnauthorizedAccessException("Coupon or pricing operation is unavailable."); }
    private sealed record ProtectedCode(byte[] Content, string Key);
    private sealed record Grant(Guid Id, decimal Percent);
    private sealed record ClaimOrder(int Quantity, string Currency, decimal UnitUsdt, decimal? UnitToman, bool Automatic, FxRate? Rate, decimal? Percent, int? Units, string Version, string Status, decimal Step);
    private sealed record ClaimState(string Payment, bool Release, string Kind, string Fulfillment, string? Resolution);
}
