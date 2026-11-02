using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

public sealed record CheckoutRequest(Guid OrderId, Guid ProductId, int Quantity, CheckoutPayment Payment = CheckoutPayment.Wallet, CheckoutKind Kind = CheckoutKind.ProductPurchase, CryptoAsset? Network = null);

public sealed partial class CheckoutStore
{
    public async Task<CheckoutResult?> ReplayAsync(BrandContext context, long actor, CheckoutRequest request, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginAsync(ct);
        return await ReplayLockedAsync(session, context, actor, request, ct);
    }

    internal async Task<CheckoutResult?> ReplayLockedAsync(DbSession session, BrandContext context, long actor, CheckoutRequest request, CancellationToken ct)
    {
        if (request.OrderId == Guid.Empty || request.ProductId == Guid.Empty || request.Quantity is < 1 or > 10000 ||
            request.Payment is not (CheckoutPayment.Wallet or CheckoutPayment.Crypto or CheckoutPayment.Bank) || !Enum.IsDefined(request.Kind) ||
            (request.Network is not null && (request.Payment != CheckoutPayment.Crypto || !Enum.IsDefined(request.Network.Value)))) throw new ArgumentException("Invalid checkout request.");
        await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM (SELECT id FROM shop.users WHERE brand_id=$1 AND telegram_id=$2 FOR UPDATE) u", DbValue.Uuid(context.BrandId), DbValue.Bigint(actor)), row => row.GetInt64(0), ct);
        BrandUser member = await BrandUserStore.RequireAsync(session, context, actor, null, ct);
        if (member.Blocked || member.Banned) Reject(session);
        await session.ExecuteAsync(new DbStatement("SELECT pg_advisory_xact_lock($1)", DbValue.Bigint(BinaryPrimitives.ReadInt64BigEndian(request.OrderId.ToByteArray(bigEndian: true)))), ct);
        var prior = await session.QuerySingleAsync(new DbStatement("""
            SELECT o.id,a.id,a.actor_user_id,a.brand_id,a.command_sha256 FROM (SELECT 1) seed
            LEFT JOIN shop.orders o ON o.id=$1 LEFT JOIN shop.audit_events a ON a.id=$1
            """, DbValue.Uuid(request.OrderId)), row => new Prior(row.IsDBNull(0), row.IsDBNull(1), row.IsDBNull(2) ? null : row.GetGuid(2), row.IsDBNull(3) ? null : row.GetGuid(3), row.IsDBNull(4) ? null : row.GetBinary(4)), ct);
        if (prior.NoOrder && prior.NoAudit) return null;
        if (prior.NoOrder || prior.NoAudit || prior.Member != member.Id || prior.Brand != context.BrandId || prior.Hash is null ||
            !CryptographicOperations.FixedTimeEquals(prior.Hash, Fingerprint(context, member.Id, request))) Reject(session);
        return new(CheckoutOutcome.Replayed, request.OrderId);
    }

    internal async Task<CheckoutResult> AcceptPreparedAsync(DbSession session, BrandContext context, long actor, CheckoutRequest request,
        PreparedCheckout prepared, CatalogStore catalog, CancellationToken ct)
    {
        if (prepared.Query != new CatalogQuery(request.ProductId, request.Quantity, request.Payment, request.Kind)) Reject(session);
        CheckoutQuote quote = prepared.Quote;
        QuoteTerms terms = quote.Terms;
        bool free = terms.Payment == CheckoutPayment.NoPaymentRequired;
        bool unpaid = terms.Payment is CheckoutPayment.Crypto or CheckoutPayment.Bank;
        if (!free && quote.Payable.Amount <= 0) throw new PricingRejectedException();
        if (request.Kind == CheckoutKind.PrepaidUnits && free) return new(CheckoutOutcome.Unavailable);
        var product = await session.QuerySingleAsync(new DbStatement("SELECT fulfillment_kind,local_first,availability_policy FROM shop.products WHERE id=$1", DbValue.Uuid(request.ProductId)),
            row => (Kind: row.GetString(0), LocalFirst: row.GetBoolean(1), Preorder: row.GetString(2) == "local_preorder"), ct);
        var pending = await session.QueryManyAsync(new DbStatement("""
            SELECT o.id,o.payment_status FROM shop.orders o WHERE o.user_id=$1 AND o.payment_status='awaiting_payment' ORDER BY o.id FOR UPDATE
            """, DbValue.Uuid(prepared.Member)), row => (Id: row.GetGuid(0), State: row.GetString(1)), 1000, ct);
        await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM (SELECT p.id FROM shop.payments p JOIN shop.orders o ON o.id=p.order_id WHERE o.user_id=$1 AND o.payment_status='awaiting_payment' ORDER BY p.id FOR UPDATE OF p) locked", DbValue.Uuid(prepared.Member)), row => row.GetInt64(0), ct);
        bool evidence = await session.QuerySingleAsync(new DbStatement("""
            SELECT EXISTS(SELECT 1 FROM shop.payments p JOIN shop.orders o ON o.id=p.order_id WHERE o.user_id=$1 AND o.payment_status='awaiting_payment'
                AND (p.first_payment_observed_at IS NOT NULL OR p.receipt_asset_ref IS NOT NULL OR p.status IN ('observed','confirming','settled','review')))
            """, DbValue.Uuid(prepared.Member)), row => row.GetBoolean(0), ct);
        if (evidence) return new(CheckoutOutcome.Unavailable);
        var wallet = await session.QuerySingleAsync(new DbStatement("""
            SELECT w.balance::text,w.version FROM (SELECT 1) seed LEFT JOIN (SELECT * FROM shop.wallets WHERE user_id=$1 FOR UPDATE) w ON true
            """, DbValue.Uuid(prepared.Member)), row => (Balance: row.IsDBNull(0) ? new Money(0, Currency.Usd) : DbValue.ReadMoney(row, 0, Currency.Usd), Version: row.IsDBNull(1) ? 0 : row.GetInt64(1)), ct);
        if (!free && !unpaid && wallet.Balance.Amount < quote.TotalUsd.Amount) return new(CheckoutOutcome.InsufficientBalance);
        int local = unpaid || request.Kind == CheckoutKind.PrepaidUnits ? 0 : prepared.Supply.LocalUnits;
        var stock = new List<Stock>(local);
        while (stock.Count < local)
        {
            int batch = Math.Min(1000, local - stock.Count);
            var rows = await session.QueryManyAsync(new DbStatement("SELECT id,credential_ciphertext,credential_key_id FROM shop.inventory_items WHERE product_id=$1 AND state='available' ORDER BY id LIMIT $2 OFFSET $3 FOR UPDATE",
                DbValue.Uuid(request.ProductId), DbValue.Integer(batch), DbValue.Integer(stock.Count)), row => new Stock(row.GetGuid(0), row.GetBinary(1), row.GetString(2)), batch, ct);
            if (rows.Count != batch) return new(CheckoutOutcome.OutOfStock);
            stock.AddRange(rows);
        }
        if (System.Diagnostics.Stopwatch.GetElapsedTime(prepared.IssuedAt) > TimeSpan.FromSeconds(30)) Reject(session);
        var pricing = new PricingStore(sessions, secrets);
        foreach (var old in pending)
        {
            await session.ExecuteAsync(new DbStatement("UPDATE shop.orders SET payment_status='cancelled',fulfillment_status='cancelled',resolution_kind='replaced',resolution_at=clock_timestamp(),revision=revision+1,updated_at=clock_timestamp() WHERE id=$1", DbValue.Uuid(old.Id)), ct);
            Guid? held = await session.QuerySingleAsync(new DbStatement("SELECT discount_id FROM (SELECT 1) seed LEFT JOIN shop.discount_uses ON order_id=$1 AND status='held'", DbValue.Uuid(old.Id)), row => row.IsDBNull(0) ? (Guid?)null : row.GetGuid(0), ct);
            if (held is Guid discount) await pricing.TransitionClaimAsync(session, context, discount, old.Id, false, ct);
            await session.ExecuteAsync(new DbStatement("UPDATE shop.jobs SET status='cancelled',completed_at=clock_timestamp() WHERE order_id=$1 AND job_kind='expire_order_payment' AND status IN ('pending','retry')", DbValue.Uuid(old.Id)), ct);
            await PaymentInstructionStore.EnqueueAsync(session, context, old.Id, "replaced", ct);
        }
        await session.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.audit_events(id,actor_kind,actor_user_id,brand_id,action,entity_kind,entity_id,reason,command_sha256,correlation_id)
            VALUES ($1,'customer',$2,$3,'create','order',$1,'customer_request',$4,gen_random_uuid())
            """, DbValue.Uuid(request.OrderId), DbValue.Uuid(prepared.Member), DbValue.Uuid(context.BrandId), DbValue.Binary(Fingerprint(context, prepared.Member, request))), ct);
        await session.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.orders(id,user_id,product_id,quantity,order_kind,payment_status,currency,payment_method,unit_price_usdt,unit_price_toman,
                fx_rate_snapshot,fx_source_snapshot,fx_fetched_at_snapshot,auto_price_toman_snapshot,pricing_rounding_step_snapshot,price_calculation_version,
                discount_pct_snapshot,discount_units_snapshot,brand_public_name_snapshot,product_title_snapshot,product_description_snapshot,product_icon_snapshot,
                product_guidance_snapshot,payment_terms_snapshot,fulfillment_kind_snapshot,local_first_snapshot,is_preorder,payment_confirmed_at,fulfillment_status,payment_due_at)
            VALUES ($1,$2,$3,$4,$26,$5,$27,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21,
                'Frozen quote; complete-order delivery',$22,$23,$24,CASE WHEN $28 THEN NULL ELSE CURRENT_TIMESTAMP END,$25,CASE WHEN $28 THEN CURRENT_TIMESTAMP+INTERVAL '30 minutes' ELSE NULL END)
            """, DbValue.Uuid(request.OrderId), DbValue.Uuid(prepared.Member), DbValue.Uuid(request.ProductId), DbValue.Integer(request.Quantity), DbValue.Text(unpaid ? "awaiting_payment" : free ? "not_required" : "paid"),
            DbValue.Text(free ? "no_payment_required" : terms.Payment.ToString().ToLowerInvariant()), DbValue.Numeric(terms.UnitUsdt), Numeric(terms.UnitToman), Numeric(terms.Rate?.Toman), NullableText(terms.Rate?.Source), Time(terms.Rate?.FetchedAt),
            DbValue.Boolean(terms.AutoToman), DbValue.Numeric(terms.RoundingStep), DbValue.Text(terms.Version), Numeric(terms.DiscountPercent), Integer(terms.DiscountUnits),
            DbValue.Text(terms.Product.Brand), DbValue.Text(terms.Product.Title), NullableText(terms.Product.Description), NullableText(terms.Product.Icon), NullableText(terms.Product.Guidance),
            DbValue.Text(product.Kind), DbValue.Boolean(product.LocalFirst), DbValue.Boolean(request.Kind == CheckoutKind.ProductPurchase && product.Preorder), DbValue.Text(unpaid || request.Kind == CheckoutKind.PrepaidUnits ? "unfulfilled" : prepared.Supply.WaitingForLocalStock ? "waiting_stock" : product.Kind == "manual" ? "requires_admin" : "acquiring"),
            DbValue.Text(request.Kind == CheckoutKind.PrepaidUnits ? "prepaid_units" : "product_purchase"), DbValue.Text(terms.Currency.ToCode()), DbValue.Boolean(unpaid)), ct);
        await BotActivityWriter.CheckoutAsync(session, context, actor, request.OrderId, ct);
        if (terms.DiscountId is Guid coupon) await pricing.HoldAsync(session, context, actor, coupon, request.OrderId, ct);
        if (!free && !unpaid)
        {
            Money balance = wallet.Balance.Subtract(quote.TotalUsd);
            await session.ExecuteAsync(new DbStatement("UPDATE shop.wallets SET balance=$1,version=version+1,updated_at=clock_timestamp() WHERE user_id=$2", DbValue.Money(balance, Currency.Usd), DbValue.Uuid(prepared.Member)), ct);
            await session.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.wallet_entries(id,user_id,signed_amount,balance_after,posting_version,movement_kind,is_posting,idempotency_key,order_id,reason)
                VALUES (gen_random_uuid(),$1,$2,$3,$4,'purchase_debit',true,$5,$6,'customer_request')
                """, DbValue.Uuid(prepared.Member), DbValue.Numeric(-quote.TotalUsd.Amount), DbValue.Money(balance, Currency.Usd), DbValue.Bigint(checked(wallet.Version + 1)), DbValue.Text("checkout:" + request.OrderId.ToString("D")), DbValue.Uuid(request.OrderId)), ct);
        }
        if (unpaid)
        {
            await CreatePaymentAsync(session, context, request, quote, ct);
            DateTime due = await session.QuerySingleAsync(new DbStatement("SELECT payment_due_at FROM shop.orders WHERE id=$1", DbValue.Uuid(request.OrderId)), row => row.GetUtc(0), ct);
            await OutboxStore.EnqueueAsync(session, JobIntent.Order(JobKind.ExpireOrderPayment, context.BrandId, request.OrderId), request.OrderId, due, ct);
            await PaymentInstructionStore.EnqueueAsync(session, context, request.OrderId, "created", ct);
            return new(CheckoutOutcome.AwaitingPayment, request.OrderId);
        }
        if (request.Kind == CheckoutKind.PrepaidUnits)
        {
            await pricing.IssuePaidSourceAsync(session, context, request.OrderId, ct);
            return new(CheckoutOutcome.Purchased, request.OrderId);
        }
        if (prepared.Supply.RemoteUnits > 0) await catalog.BindSupplierAsync(session, context, prepared, request.OrderId, ct);
        await AcquireAsync(session, context, request.OrderId, stock, ct);
        if (prepared.Supply.RemoteUnits == 0 && local == request.Quantity) await OutboxStore.EnqueueAsync(session, JobIntent.Order(JobKind.PrepareManifest, context.BrandId, request.OrderId), request.OrderId, DateTime.UtcNow, ct);
        else if (prepared.Supply.WaitingForLocalStock) await OutboxStore.EnqueueAsync(session, JobIntent.Product(JobKind.DrainLocalPreorders, request.ProductId), request.OrderId, DateTime.UtcNow, ct);
        return new(CheckoutOutcome.Purchased, request.OrderId);
    }

    internal async Task AcquireAsync(DbSession session, BrandContext context, Guid order, IReadOnlyList<Stock> stock, CancellationToken ct)
    {
        var inventory = new InventoryCipher(secrets);
        var cipher = new BrandCipher(secrets);
        int unit = 0;
        foreach (Stock item in stock)
        {
            Guid fulfillment = Guid.CreateVersion7();
            byte[] plain = inventory.Decrypt(item.Id, item.Key, item.Content);
            byte[] encrypted;
            try { encrypted = cipher.Encrypt(context, "fulfillments", fulfillment, "delivered_content", plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            await session.ExecuteAsync(new DbStatement("INSERT INTO shop.fulfillments(id,order_id,unit_no,kind) VALUES ($1,$2,$3,'local')", DbValue.Uuid(fulfillment), DbValue.Uuid(order), DbValue.Integer(++unit)), ct);
            await session.ExecuteAsync(new DbStatement("UPDATE shop.inventory_items SET state='allocated',assigned_fulfillment_id=$1,allocated_at=clock_timestamp() WHERE id=$2", DbValue.Uuid(fulfillment), DbValue.Uuid(item.Id)), ct);
            await session.ExecuteAsync(new DbStatement("UPDATE shop.fulfillments SET status='acquired',delivered_content=$1,delivered_content_key_id=$2,content_kind='text' WHERE id=$3", DbValue.Binary(encrypted), DbValue.Text(secrets.CurrentBrandKeyId(context)), DbValue.Uuid(fulfillment)), ct);
        }
    }
    private static byte[] Fingerprint(BrandContext context, Guid member, CheckoutRequest request) => SHA256.HashData(Encoding.ASCII.GetBytes(
        $"prepared-goods-v1:{context.BrandId:D}:{member:D}:{request.ProductId:D}:{request.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{request.Payment}" + (request.Kind == CheckoutKind.PrepaidUnits ? ":prepaid" : "") + (request.Network is CryptoAsset network ? ":" + network : "")));
    private static DbValue Numeric(decimal? value) => value is decimal number ? DbValue.Numeric(number) : DbValue.Null(NpgsqlTypes.NpgsqlDbType.Numeric);
    private static DbValue Integer(int? value) => value is int number ? DbValue.Integer(number) : DbValue.Null(NpgsqlTypes.NpgsqlDbType.Integer);
    private static DbValue Time(DateTime? value) => value is DateTime time ? DbValue.Utc(time) : DbValue.Null(NpgsqlTypes.NpgsqlDbType.TimestampTz);
}
