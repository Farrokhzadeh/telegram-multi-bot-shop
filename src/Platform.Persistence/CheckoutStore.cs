using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

public enum CheckoutOutcome { Purchased, Replayed, Unavailable, QuoteChanged, InsufficientBalance, OutOfStock, AwaitingPayment }
public sealed record LocalCheckoutRequest(Guid OrderId, Guid ProductId, int Quantity, decimal UnitPrice, long ListingRevision = 0, long ProductRevision = 0, long PolicyRevision = 0);
public sealed record CheckoutResult(CheckoutOutcome Outcome, Guid? OrderId = null);

public sealed partial class CheckoutStore(DbSessionFactory sessions, SecretSnapshot secrets)
{
    public async Task<CheckoutResult> PurchaseLocalAsync(BrandContext context, long verifiedActorId, LocalCheckoutRequest request, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginAsync(ct);
        CheckoutResult result = await AcceptLocalAsync(session, context, verifiedActorId, request, ct);
        await session.CommitAsync(ct);
        return result;
    }

    public async Task<CheckoutResult> AcceptLocalAsync(DbSession session, BrandContext context, long verifiedActorId, LocalCheckoutRequest request, CancellationToken ct = default)
    {
        try { return await AcceptCoreAsync(session, context, verifiedActorId, request, ct); }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }

    private async Task<CheckoutResult> AcceptCoreAsync(DbSession session, BrandContext context, long verifiedActorId, LocalCheckoutRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.OrderId == Guid.Empty || request.ProductId == Guid.Empty || request.Quantity is < 1 or > 10000 || request.UnitPrice <= 0 ||
            request.ListingRevision < 0 || request.ProductRevision < 0 || request.PolicyRevision < 0) throw new ArgumentException("Invalid checkout request.");
        await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM (SELECT id FROM shop.users WHERE brand_id=$1 AND telegram_id=$2 FOR UPDATE) u", DbValue.Uuid(context.BrandId), DbValue.Bigint(verifiedActorId)), row => row.GetInt64(0), ct);
        BrandUser member = await BrandUserStore.RequireAsync(session, context, verifiedActorId, null, ct);
        if (member.Blocked || member.Banned) Reject(session);
        await session.ExecuteAsync(new DbStatement("SELECT pg_advisory_xact_lock($1)", DbValue.Bigint(BinaryPrimitives.ReadInt64BigEndian(request.OrderId.ToByteArray(bigEndian: true)))), ct);
        byte[] fingerprint = Fingerprint(context, member.Id, request);
        var prior = await session.QuerySingleAsync(new DbStatement("""
            SELECT o.id,a.id,a.actor_user_id,a.brand_id,a.command_sha256 FROM (SELECT 1) seed
            LEFT JOIN shop.orders o ON o.id=$1 LEFT JOIN shop.audit_events a ON a.id=$1
            """, DbValue.Uuid(request.OrderId)), row => new Prior(row.IsDBNull(0), row.IsDBNull(1), row.IsDBNull(2) ? null : row.GetGuid(2), row.IsDBNull(3) ? null : row.GetGuid(3), row.IsDBNull(4) ? null : row.GetBinary(4)), ct);
        if (!prior.NoOrder || !prior.NoAudit)
        {
            if (prior.NoOrder || prior.NoAudit || prior.Member != member.Id || prior.Brand != context.BrandId || prior.Hash is null || !CryptographicOperations.FixedTimeEquals(prior.Hash, fingerprint)) Reject(session);
            return new(CheckoutOutcome.Replayed, request.OrderId);
        }
        var product = await session.QuerySingleAsync<Product?>(new DbStatement("""
            SELECT p.id,p.active,p.fulfillment_kind,p.availability_policy,p.local_first,p.revision,p.title,p.description,p.icon_ref,p.guidance
            FROM (SELECT 1) seed LEFT JOIN (SELECT * FROM shop.products WHERE id=$1 FOR UPDATE) p ON true
            """, DbValue.Uuid(request.ProductId)), row => row.IsDBNull(0) ? null : new Product(row.GetBoolean(1), row.GetString(2), row.GetString(3), row.GetBoolean(4), row.GetInt64(5), row.GetString(6), Text(row, 7), Text(row, 8), Text(row, 9)), ct);
        if (product is null || !product.Active || product.Kind != "local" || product.Availability != "immediate") return new(CheckoutOutcome.Unavailable);
        var brand = await session.QuerySingleAsync(new DbStatement("SELECT public_name,policy_version,allow_wallet,require_channel_join FROM shop.brands WHERE id=$1 AND bot_id=$2 AND active FOR UPDATE", DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId)),
            row => (Name: row.GetString(0), Revision: row.GetInt64(1), Wallet: row.GetBoolean(2), Channels: row.GetBoolean(3)), ct);
        if (!brand.Wallet || brand.Channels) return new(CheckoutOutcome.Unavailable);
        var listing = await session.QuerySingleAsync<Listing?>(new DbStatement("""
            SELECT b.product_id,b.visible,b.allow_wallet,b.price_usdt::text,b.min_quantity,b.max_quantity,b.revision,
                CASE WHEN b.title_mode='override' THEN b.title_override ELSE $3 END,
                CASE b.description_mode WHEN 'override' THEN b.description_override WHEN 'clear' THEN NULL ELSE $4 END,
                CASE b.icon_mode WHEN 'override' THEN b.icon_override WHEN 'clear' THEN NULL ELSE $5 END,
                CASE b.guidance_mode WHEN 'override' THEN b.guidance_override WHEN 'clear' THEN NULL ELSE $6 END
            FROM (SELECT 1) seed LEFT JOIN (SELECT * FROM shop.brand_products WHERE brand_id=$1 AND product_id=$2 FOR UPDATE) b ON true
            """, DbValue.Uuid(context.BrandId), DbValue.Uuid(request.ProductId), DbValue.Text(product.Title), NullableText(product.Description), NullableText(product.Icon), NullableText(product.Guidance)),
            row => row.IsDBNull(0) ? null : new Listing(row.GetBoolean(1), row.GetBoolean(2), ExactDecimal.Parse(row.GetString(3)), row.GetInt32(4), row.GetInt32(5), row.GetInt64(6), row.GetString(7), Text(row, 8), Text(row, 9), Text(row, 10)), ct);
        if (listing is null || !listing.Visible || !listing.Wallet || request.Quantity < listing.Min || request.Quantity > listing.Max) return new(CheckoutOutcome.Unavailable);
        if (listing.Price != request.UnitPrice || listing.Revision != request.ListingRevision || product.Revision != request.ProductRevision || brand.Revision != request.PolicyRevision) return new(CheckoutOutcome.QuoteChanged);
        bool guarded = await session.QuerySingleAsync(new DbStatement("""
            SELECT EXISTS(SELECT 1 FROM shop.orders WHERE user_id=$1 AND payment_status IN ('awaiting_payment','under_review','confirmation_pending'))
                OR EXISTS(SELECT 1 FROM shop.orders WHERE product_id=$2 AND is_preorder AND payment_status IN ('paid','not_required') AND fulfillment_status IN ('waiting_stock','requires_admin'))
                OR EXISTS(SELECT 1 FROM shop.discounts WHERE brand_id=$3 AND active AND (product_id IS NULL OR product_id=$2) AND (eligible_user_id IS NULL OR eligible_user_id=$1)
                    AND (starts_at IS NULL OR starts_at<=clock_timestamp()) AND (ends_at IS NULL OR ends_at>clock_timestamp()))
            """, DbValue.Uuid(member.Id), DbValue.Uuid(request.ProductId), DbValue.Uuid(context.BrandId)), row => row.GetBoolean(0), ct);
        if (guarded) return new(CheckoutOutcome.Unavailable);
        Money total = new Money(listing.Price, Currency.Usd).MultiplyQuantity(request.Quantity);
        var wallet = await session.QuerySingleAsync(new DbStatement("""
            SELECT w.balance::text,w.version FROM (SELECT 1) seed LEFT JOIN (SELECT * FROM shop.wallets WHERE user_id=$1 FOR UPDATE) w ON true
            """, DbValue.Uuid(member.Id)), row => (Balance: row.IsDBNull(0) ? Money.Parse("0", Currency.Usd) : DbValue.ReadMoney(row, 0, Currency.Usd), Version: row.IsDBNull(1) ? 0 : row.GetInt64(1)), ct);
        if (wallet.Balance.Amount < total.Amount) return new(CheckoutOutcome.InsufficientBalance);
        var stock = await session.QueryManyAsync(new DbStatement("SELECT id,credential_ciphertext,credential_key_id FROM shop.inventory_items WHERE product_id=$1 AND state='available' ORDER BY id LIMIT $2 FOR UPDATE", DbValue.Uuid(request.ProductId), DbValue.Integer(request.Quantity)),
            row => new Stock(row.GetGuid(0), row.GetBinary(1), row.GetString(2)), request.Quantity, ct);
        if (stock.Count != request.Quantity) return new(CheckoutOutcome.OutOfStock);
        await session.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.audit_events(id,actor_kind,actor_user_id,brand_id,action,entity_kind,entity_id,reason,command_sha256,correlation_id)
            VALUES ($1,'customer',$2,$3,'create','order',$1,'customer_request',$4,gen_random_uuid())
            """, DbValue.Uuid(request.OrderId), DbValue.Uuid(member.Id), DbValue.Uuid(context.BrandId), DbValue.Binary(fingerprint)), ct);
        await session.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.orders(id,user_id,product_id,quantity,order_kind,payment_status,currency,payment_method,unit_price_usdt,auto_price_toman_snapshot,
                pricing_rounding_step_snapshot,price_calculation_version,brand_public_name_snapshot,product_title_snapshot,product_description_snapshot,
                product_icon_snapshot,product_guidance_snapshot,payment_terms_snapshot,fulfillment_kind_snapshot,local_first_snapshot,is_preorder,payment_confirmed_at)
            VALUES ($1,$2,$3,$4,'product_purchase','paid','USDT','wallet',$5,false,5000,'local-wallet-v1',$6,$7,$8,$9,$10,'USD store credit; complete-order delivery','local',$11,false,CURRENT_TIMESTAMP)
            """, DbValue.Uuid(request.OrderId), DbValue.Uuid(member.Id), DbValue.Uuid(request.ProductId), DbValue.Integer(request.Quantity), DbValue.Numeric(listing.Price),
            DbValue.Text(brand.Name), DbValue.Text(listing.Title), NullableText(listing.Description), NullableText(listing.Icon), NullableText(listing.Guidance), DbValue.Boolean(product.LocalFirst)), ct);
        await BotActivityWriter.CheckoutAsync(session, context, verifiedActorId, request.OrderId, ct);
        Money balance = wallet.Balance.Subtract(total);
        await session.ExecuteAsync(new DbStatement("UPDATE shop.wallets SET balance=$1,version=version+1,updated_at=CURRENT_TIMESTAMP WHERE user_id=$2", DbValue.Money(balance, Currency.Usd), DbValue.Uuid(member.Id)), ct);
        await session.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.wallet_entries(id,user_id,signed_amount,balance_after,posting_version,movement_kind,is_posting,idempotency_key,order_id,reason)
            VALUES (gen_random_uuid(),$1,$2,$3,$4,'purchase_debit',true,$5,$6,'customer_request')
            """, DbValue.Uuid(member.Id), DbValue.Numeric(-total.Amount), DbValue.Money(balance, Currency.Usd), DbValue.Bigint(checked(wallet.Version + 1)), DbValue.Text("checkout:" + request.OrderId.ToString("D")), DbValue.Uuid(request.OrderId)), ct);
        await AcquireAsync(session, context, request.OrderId, stock, ct);
        await OutboxStore.EnqueueAsync(session, JobIntent.Order(JobKind.PrepareManifest, context.BrandId, request.OrderId), request.OrderId, DateTime.UtcNow, ct);
        return new(CheckoutOutcome.Purchased, request.OrderId);
    }

    private static byte[] Fingerprint(BrandContext context, Guid member, LocalCheckoutRequest request)
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("local-wallet-v1"); writer.Write(context.BrandId.ToByteArray()); writer.Write(member.ToByteArray()); writer.Write(request.ProductId.ToByteArray());
            writer.Write(request.Quantity); writer.Write(ExactDecimal.Format(request.UnitPrice)); writer.Write(request.ListingRevision); writer.Write(request.ProductRevision); writer.Write(request.PolicyRevision);
        }
        return SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)));
    }
    private static string? Text(DbRow row, int column) => row.IsDBNull(column) ? null : row.GetString(column);
    private static DbValue NullableText(string? value) => value is null ? DbValue.Null(NpgsqlTypes.NpgsqlDbType.Text) : DbValue.Text(value);
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Reject(DbSession session) { session.RejectApplication(); throw new UnauthorizedAccessException("Checkout request is unavailable."); }
    private sealed record Prior(bool NoOrder, bool NoAudit, Guid? Member, Guid? Brand, byte[]? Hash);
    private sealed record Product(bool Active, string Kind, string Availability, bool LocalFirst, long Revision, string Title, string? Description, string? Icon, string? Guidance);
    private sealed record Listing(bool Visible, bool Wallet, decimal Price, int Min, int Max, long Revision, string Title, string? Description, string? Icon, string? Guidance);
    internal sealed record Stock(Guid Id, byte[] Content, string Key);
}
