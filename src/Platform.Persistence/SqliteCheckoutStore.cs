using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

public sealed class SqliteCheckoutStore(SqliteSessionFactory sessions, SecretSnapshot secrets)
{
    public Task<CheckoutResult> PurchaseLocalAsync(BrandContext context, long expectedRevision, long actor, LocalCheckoutRequest request, CancellationToken ct) =>
        sessions.RunAsync(true, (_, tx, token) => AcceptLocalAsync(tx, context, expectedRevision, actor, request, token), ct);

    internal async Task<CheckoutResult> AcceptLocalAsync(SqliteTransaction tx, BrandContext context, long expectedRevision, long actor, LocalCheckoutRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        if (actor <= 0 || expectedRevision < 0 || request.OrderId == Guid.Empty || request.ProductId == Guid.Empty ||
            request.Quantity is < 1 or > 10000 || request.UnitPrice <= 0 || request.ListingRevision < 0 ||
            request.ProductRevision < 0 || request.PolicyRevision < 0) throw new ArgumentException("Invalid checkout request.");

        string now = SqliteSchema.UtcText(DateTime.UtcNow);
        Guid member;
        string brandName;
        bool walletAllowed;
        using (var lookup = Command("""
            SELECT u.id,b.public_name,b.allow_wallet AND NOT b.require_channel_join
            FROM users u JOIN brands b ON b.id=u.brand_id
            WHERE b.id=$brand AND b.bot_id=$bot AND b.active AND b.policy_version=$revision
              AND u.telegram_id=$actor AND u.blocked_at IS NULL AND u.banned_at IS NULL
            """, ("$brand", context.BrandId), ("$bot", context.BotId), ("$revision", expectedRevision), ("$actor", actor)))
        await using (var row = await lookup.ExecuteReaderAsync(ct))
        {
            if (!await row.ReadAsync(ct)) throw Unavailable();
            member = row.GetGuid(0);
            brandName = row.GetString(1);
            walletAllowed = row.GetBoolean(2);
        }

        byte[] proof = CheckoutStore.Fingerprint(context, member, request);
        using (var replay = Command("""
            SELECT o.id,a.actor_user_id,a.brand_id,a.command_sha256,a.id
            FROM (SELECT 1) LEFT JOIN orders o ON o.id=$order LEFT JOIN audit_events a ON a.id=$order
            """, ("$order", request.OrderId)))
        await using (var row = await replay.ExecuteReaderAsync(ct))
        {
            await row.ReadAsync(ct);
            if (!row.IsDBNull(0) || !row.IsDBNull(4))
            {
                if (row.IsDBNull(0) || row.IsDBNull(1) || row.IsDBNull(2) || row.IsDBNull(3) ||
                    row.GetGuid(1) != member || row.GetGuid(2) != context.BrandId ||
                    !CryptographicOperations.FixedTimeEquals(row.GetFieldValue<byte[]>(3), proof)) throw Unavailable();
                return new(CheckoutOutcome.Replayed, request.OrderId);
            }
        }

        if (!walletAllowed) return new(CheckoutOutcome.Unavailable);
        decimal price;
        bool localFirst;
        string title;
        string? description, icon, guidance;
        using (var lookup = Command("""
            SELECT l.price_usdt,l.revision,p.revision,p.local_first,
                CASE WHEN l.title_mode='override' THEN l.title_override ELSE p.title END,
                CASE l.description_mode WHEN 'override' THEN l.description_override WHEN 'clear' THEN NULL ELSE p.description END,
                CASE l.icon_mode WHEN 'override' THEN l.icon_override WHEN 'clear' THEN NULL ELSE p.icon_ref END,
                CASE l.guidance_mode WHEN 'override' THEN l.guidance_override WHEN 'clear' THEN NULL ELSE p.guidance END
            FROM products p JOIN brand_products l ON l.product_id=p.id
            WHERE p.id=$product AND l.brand_id=$brand AND p.active AND p.fulfillment_kind='local'
              AND p.availability_policy='immediate' AND l.visible AND l.allow_wallet AND $quantity BETWEEN l.min_quantity AND l.max_quantity
            """, ("$product", request.ProductId), ("$brand", context.BrandId), ("$quantity", request.Quantity)))
        await using (var row = await lookup.ExecuteReaderAsync(ct))
        {
            if (!await row.ReadAsync(ct)) return new(CheckoutOutcome.Unavailable);
            price = ExactDecimal.Parse(row.GetString(0));
            if (price != request.UnitPrice || row.GetInt64(1) != request.ListingRevision ||
                row.GetInt64(2) != request.ProductRevision || expectedRevision != request.PolicyRevision) return new(CheckoutOutcome.QuoteChanged);
            localFirst = row.GetBoolean(3);
            title = row.GetString(4);
            description = Text(row, 5);
            icon = Text(row, 6);
            guidance = Text(row, 7);
        }

        using (var guard = Command("""
            SELECT EXISTS(SELECT 1 FROM orders WHERE user_id=$member AND payment_status IN ('awaiting_payment','under_review','confirmation_pending'))
              OR EXISTS(SELECT 1 FROM orders WHERE product_id=$product AND is_preorder AND payment_status IN ('paid','not_required')
                AND fulfillment_status IN ('waiting_stock','requires_admin'))
              OR EXISTS(SELECT 1 FROM discounts WHERE brand_id=$brand AND active AND (product_id IS NULL OR product_id=$product)
                AND (eligible_user_id IS NULL OR eligible_user_id=$member) AND (starts_at IS NULL OR starts_at<=$now) AND (ends_at IS NULL OR ends_at>$now))
            """, ("$member", member), ("$product", request.ProductId), ("$brand", context.BrandId), ("$now", now)))
            if ((long)(await guard.ExecuteScalarAsync(ct))! != 0) return new(CheckoutOutcome.Unavailable);

        Money total = new Money(price, Currency.Usd).MultiplyQuantity(request.Quantity);
        var wallet = await SqliteWallet.ReadAsync(tx, member, ct);
        if (wallet.Balance.Amount < total.Amount) return new(CheckoutOutcome.InsufficientBalance);

        var stock = new List<CheckoutStore.Stock>();
        using (var lookup = Command("""
            SELECT id,credential_ciphertext,credential_key_id FROM inventory_items
            WHERE product_id=$product AND state='available' ORDER BY id LIMIT $quantity
            """, ("$product", request.ProductId), ("$quantity", request.Quantity)))
        await using (var row = await lookup.ExecuteReaderAsync(ct))
            while (await row.ReadAsync(ct)) stock.Add(new(row.GetGuid(0), row.GetFieldValue<byte[]>(1), row.GetString(2)));
        if (stock.Count != request.Quantity) return new(CheckoutOutcome.OutOfStock);

        await Write("""
            INSERT INTO audit_events(id,actor_kind,actor_user_id,brand_id,action,entity_kind,entity_id,reason,command_sha256,correlation_id)
            VALUES ($order,'customer',$member,$brand,'create','order',$order,'customer_request',$proof,$correlation)
            """, ("$order", request.OrderId), ("$member", member), ("$brand", context.BrandId), ("$proof", proof), ("$correlation", Guid.NewGuid()));
        await Write("""
            INSERT INTO orders(id,user_id,product_id,quantity,order_kind,payment_status,currency,payment_method,unit_price_usdt,auto_price_toman_snapshot,
                pricing_rounding_step_snapshot,price_calculation_version,brand_public_name_snapshot,product_title_snapshot,product_description_snapshot,
                product_icon_snapshot,product_guidance_snapshot,payment_terms_snapshot,fulfillment_kind_snapshot,local_first_snapshot,is_preorder,payment_confirmed_at,created_at,updated_at)
            VALUES ($order,$member,$product,$quantity,'product_purchase','paid','USDT','wallet',$price,false,'5000','local-wallet-v1',
                $brandName,$title,$description,$icon,$guidance,'USD store credit; complete-order delivery','local',$localFirst,false,$now,$now,$now)
            """, ("$order", request.OrderId), ("$member", member), ("$product", request.ProductId), ("$quantity", request.Quantity),
            ("$price", ExactDecimal.Format(price)), ("$brandName", brandName), ("$title", title), ("$description", description),
            ("$icon", icon), ("$guidance", guidance), ("$localFirst", localFirst), ("$now", now));
        await Write("""
            INSERT INTO bot_activity_events(id,user_id,event_type,source_kind,action_code,product_id,order_id,quantity_selected,outcome_code,idempotency_key,occurred_at)
            VALUES ($id,$member,'checkout','flow','checkout_created',$product,$order,$quantity,'accepted',$key,$now)
            """, ("$id", Guid.NewGuid()), ("$member", member), ("$product", request.ProductId), ("$order", request.OrderId),
            ("$quantity", request.Quantity), ("$key", $"workflow:checkout_created:{request.OrderId:N}"), ("$now", now));

        await SqliteWallet.PostAsync(tx, member, wallet, new Money(-total.Amount, Currency.Usd), request.OrderId, false, now, ct);

        var inventory = new InventoryCipher(secrets);
        var cipher = new BrandCipher(secrets);
        int unit = 0;
        foreach (var item in stock)
        {
            Guid fulfillment = Guid.CreateVersion7();
            byte[] plain = inventory.Decrypt(item.Id, item.Key, item.Content);
            byte[] encrypted;
            try { encrypted = cipher.Encrypt(context, "fulfillments", fulfillment, "delivered_content", plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }

            await Write("INSERT INTO fulfillments(id,order_id,unit_no,kind) VALUES ($id,$order,$unit,'local')",
                ("$id", fulfillment), ("$order", request.OrderId), ("$unit", ++unit));
            await Write("""
                UPDATE inventory_items SET state='allocated',assigned_fulfillment_id=$id,allocated_at=$now WHERE id=$stock AND state='available'
                """, ("$id", fulfillment), ("$now", now), ("$stock", item.Id));
            await Write("""
                UPDATE fulfillments SET status='acquired',delivered_content=$content,delivered_content_key_id=$key,content_kind='text' WHERE id=$id
                """, ("$content", encrypted), ("$key", secrets.CurrentBrandKeyId(context)), ("$id", fulfillment));
        }
        await OutboxStore.EnqueueSqliteAsync(tx, JobIntent.Order(JobKind.PrepareManifest, context.BrandId, request.OrderId), request.OrderId, DateTime.UtcNow, ct);
        return new(CheckoutOutcome.Purchased, request.OrderId);

        SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters) =>
            SqliteWallet.Command(tx, sql, parameters);

        async Task Write(string sql, params (string Name, object? Value)[] parameters)
        {
            using var command = Command(sql, parameters);
            if (await command.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Checkout write lost its target.");
        }
    }

    private static string? Text(SqliteDataReader row, int i) => row.IsDBNull(i) ? null : row.GetString(i);
    private static UnauthorizedAccessException Unavailable() => new("Checkout request is unavailable.");
}
