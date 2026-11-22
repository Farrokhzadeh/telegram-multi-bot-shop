using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

public sealed class SqlitePreparedCheckoutStore(SqliteSessionFactory sessions, SecretSnapshot secrets)
{
    public Task<CheckoutResult> PurchaseAsync(BrandContext context, long revision, long actor, CheckoutRequest request,
        PreparedCheckout prepared, SqliteCatalogStore catalog, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(catalog);
        if (revision < 0 || actor <= 0 || request.OrderId == Guid.Empty || request.ProductId == Guid.Empty ||
            request.Quantity is < 1 or > 10000 ||
            request.Payment is not (CheckoutPayment.Wallet or CheckoutPayment.Crypto or CheckoutPayment.Bank) ||
            request.Kind is not (CheckoutKind.ProductPurchase or CheckoutKind.PrepaidUnits) ||
            (request.Payment == CheckoutPayment.Crypto && request.Network is null) ||
            request.Network is CryptoAsset network && (request.Payment != CheckoutPayment.Crypto || !Enum.IsDefined(network)))
            throw new ArgumentException("The prepared checkout request is unavailable.");
        return sessions.RunAsync(true, (_, tx, token) => AcceptAsync(tx, context, revision, actor, request,
            prepared, catalog, token), ct);
    }

    private async Task<CheckoutResult> AcceptAsync(SqliteTransaction tx, BrandContext context, long revision, long actor,
        CheckoutRequest request, PreparedCheckout prepared, SqliteCatalogStore catalog, CancellationToken ct)
    {
        Guid member;
        using (var account = SqliteWallet.Command(tx, """
            SELECT u.id FROM users u JOIN brands b ON b.id=u.brand_id
            WHERE b.id=$brand AND b.bot_id=$bot AND b.active=1 AND b.policy_version=$revision
                AND u.telegram_id=$actor AND u.blocked_at IS NULL AND u.banned_at IS NULL
            """, ("$brand", context.BrandId), ("$bot", context.BotId), ("$revision", revision), ("$actor", actor)))
        {
            object? id = await account.ExecuteScalarAsync(ct);
            if (id is not string found) throw new UnauthorizedAccessException("Checkout actor is unavailable.");
            member = Guid.Parse(found);
        }
        if (prepared.Query != new CatalogQuery(request.ProductId, request.Quantity, request.Payment, request.Kind))
            throw new PricingRejectedException();
        bool local = request.Kind == CheckoutKind.ProductPurchase && request.Payment == CheckoutPayment.Wallet &&
            prepared.Supply.LocalUnits == request.Quantity && prepared.Supply.RemoteUnits == 0 &&
            !prepared.Supply.WaitingForLocalStock && prepared.Quote.Terms.DiscountId is null;
        var localRequest = new LocalCheckoutRequest(request.OrderId, request.ProductId, request.Quantity,
            prepared.Quote.Terms.UnitUsdt, prepared.ListingRevision, prepared.ProductRevision, prepared.PolicyRevision);
        byte[] digest = local ? CheckoutStore.Fingerprint(context, member, localRequest) :
            PreparedFingerprint(context, member, request);
        using (var previous = SqliteWallet.Command(tx, """
            SELECT o.id,a.id,a.actor_user_id,a.brand_id,a.command_sha256
            FROM (SELECT 1) seed LEFT JOIN orders o ON o.id=$order LEFT JOIN audit_events a ON a.id=$order
            """, ("$order", request.OrderId)))
        await using (var row = await previous.ExecuteReaderAsync(ct))
        {
            await row.ReadAsync(ct);
            if (!row.IsDBNull(0) || !row.IsDBNull(1))
            {
                if (row.IsDBNull(0) || row.IsDBNull(1) || row.IsDBNull(2) || row.IsDBNull(3) ||
                    row.IsDBNull(4) || row.GetGuid(2) != member || row.GetGuid(3) != context.BrandId ||
                    !CryptographicOperations.FixedTimeEquals(row.GetFieldValue<byte[]>(4), digest))
                    throw new UnauthorizedAccessException("Checkout identity was already used.");
                return new(CheckoutOutcome.Replayed, request.OrderId);
            }
        }
        CheckoutQuote quote = await catalog.RequireCurrentAsync(tx, context, actor, prepared, ct);
        QuoteTerms terms = quote.Terms;
        if (prepared.Member != member || prepared.PolicyRevision != revision ||
            terms.Payment != request.Payment || prepared.Supply.RemoteUnits != 0 ||
            quote.Payable.Amount <= 0)
            throw new PricingRejectedException();
        if (local)
            return await new SqliteCheckoutStore(sessions, secrets).AcceptLocalAsync(
                tx, context, revision, actor, localRequest, ct);
        if (request.Payment == CheckoutPayment.Wallet && request.Kind != CheckoutKind.PrepaidUnits)
            return new(CheckoutOutcome.Unavailable);
        if (request.Kind == CheckoutKind.PrepaidUnits && request.Payment == CheckoutPayment.Wallet &&
            (await SqliteWallet.ReadAsync(tx, member, ct)).Balance.Amount < quote.TotalUsd.Amount)
            return new(CheckoutOutcome.InsufficientBalance);
        DateTime instant = DateTime.UtcNow;
        string now = SqliteSchema.UtcText(instant);
        string dueAt = SqliteSchema.UtcText(instant.AddMinutes(30));
        var pending = new List<Guid>();
        using (var existing = SqliteWallet.Command(tx, """
            SELECT o.id FROM orders o WHERE o.user_id=$member AND o.payment_status='awaiting_payment'
            ORDER BY o.id
            """, ("$member", member)))
        await using (var row = await existing.ExecuteReaderAsync(ct))
            while (await row.ReadAsync(ct)) pending.Add(row.GetGuid(0));
        using (var unsafePayment = SqliteWallet.Command(tx, """
            SELECT EXISTS(SELECT 1 FROM payments p JOIN orders o ON o.id=p.order_id
                WHERE o.user_id=$member AND o.payment_status IN ('awaiting_payment','under_review','confirmation_pending')
                    AND (p.first_payment_observed_at IS NOT NULL OR p.receipt_asset_ref IS NOT NULL
                        OR p.pending_evidence_ciphertext IS NOT NULL
                        OR p.status IN ('observed','confirming','settled','review')))
                OR EXISTS(SELECT 1 FROM orders WHERE user_id=$member
                    AND payment_status IN ('under_review','confirmation_pending'))
                OR EXISTS(SELECT 1 FROM discount_uses d JOIN orders o ON o.id=d.order_id
                    WHERE o.user_id=$member AND o.payment_status='awaiting_payment' AND d.status='held')
            """, ("$member", member)))
            if ((long)(await unsafePayment.ExecuteScalarAsync(ct))! != 0)
                return new(CheckoutOutcome.Unavailable);

        string kind;
        bool localFirst;
        bool preorder;
        using (var product = SqliteWallet.Command(tx, """
            SELECT fulfillment_kind,local_first,availability_policy
            FROM products WHERE id=$product AND active=1
            """, ("$product", request.ProductId)))
        await using (var row = await product.ExecuteReaderAsync(ct))
        {
            if (!await row.ReadAsync(ct) ||
                (request.Kind == CheckoutKind.ProductPurchase &&
                    (row.GetString(0) is not ("local" or "manual") ||
                    row.GetString(2) == "local_preorder" && row.GetString(0) != "local")))
                throw new PricingRejectedException();
            kind = row.GetString(0);
            localFirst = row.GetBoolean(1);
            preorder = request.Kind == CheckoutKind.ProductPurchase &&
                row.GetString(2) == "local_preorder";
        }

        foreach (Guid old in pending)
        {
            await Change(tx, """
                UPDATE orders SET payment_status='cancelled',fulfillment_status='cancelled',
                    resolution_kind='replaced',resolution_at=$now,revision=revision+1,updated_at=$now
                WHERE id=$old AND payment_status='awaiting_payment'
                """, ct, ("$now", now), ("$old", old));
            using var cancel = SqliteWallet.Command(tx, """
                UPDATE jobs SET status='cancelled',completed_at=$now
                WHERE order_id=$old AND job_kind='expire_order_payment' AND status IN ('pending','retry')
                """, ("$now", now), ("$old", old));
            await cancel.ExecuteNonQueryAsync(ct);
            await QueueInstructionAsync(tx, context, member, old, "replaced", ct);
        }

        await Change(tx, """
            INSERT INTO audit_events(id,actor_kind,actor_user_id,brand_id,action,entity_kind,entity_id,
                reason,command_sha256,correlation_id)
            VALUES ($order,'customer',$member,$brand,'create','order',$order,'customer_request',$digest,$correlation)
            """, ct, ("$order", request.OrderId), ("$member", member), ("$brand", context.BrandId),
            ("$digest", digest), ("$correlation", Guid.CreateVersion7()));

        await Change(tx, """
            INSERT INTO orders(id,user_id,product_id,quantity,order_kind,payment_status,currency,payment_method,
                unit_price_usdt,unit_price_toman,fx_rate_snapshot,fx_source_snapshot,fx_fetched_at_snapshot,
                auto_price_toman_snapshot,pricing_rounding_step_snapshot,price_calculation_version,
                brand_public_name_snapshot,product_title_snapshot,product_description_snapshot,product_icon_snapshot,
                product_guidance_snapshot,payment_terms_snapshot,fulfillment_kind_snapshot,local_first_snapshot,
                is_preorder,payment_due_at,created_at,updated_at,
                discount_pct_snapshot,discount_units_snapshot,payment_confirmed_at)
            VALUES ($order,$member,$product,$quantity,$orderKind,$status,$currency,$method,
                $usd,$toman,$rate,$source,$fetched,$automatic,$step,$version,$brandName,$title,$description,$icon,
                $guidance,'Frozen quote; complete-order delivery',$kind,$localFirst,$preorder,$deadline,$now,$now,
                $percent,$discountUnits,$confirmed)
            """, ct, ("$order", request.OrderId), ("$member", member), ("$product", request.ProductId),
            ("$quantity", request.Quantity), ("$currency", terms.Currency.ToCode()),
            ("$method", request.Payment switch
            {
                CheckoutPayment.Bank => "bank",
                CheckoutPayment.Crypto => "crypto",
                _ => "wallet"
            }),
            ("$orderKind", request.Kind == CheckoutKind.PrepaidUnits ? "prepaid_units" : "product_purchase"),
            ("$status", request.Payment == CheckoutPayment.Wallet ? "paid" : "awaiting_payment"),
            ("$usd", ExactDecimal.Format(terms.UnitUsdt)),
            ("$toman", terms.UnitToman is decimal toman ? ExactDecimal.Format(toman) : null),
            ("$rate", terms.Rate is FxRate rate ? ExactDecimal.Format(rate.Toman) : null),
            ("$source", terms.Rate?.Source),
            ("$fetched", terms.Rate is FxRate fx ? SqliteSchema.UtcText(fx.FetchedAt) : null),
            ("$automatic", terms.AutoToman), ("$step", ExactDecimal.Format(terms.RoundingStep)),
            ("$version", terms.Version), ("$brandName", terms.Product.Brand),
            ("$title", terms.Product.Title), ("$description", terms.Product.Description),
            ("$icon", terms.Product.Icon), ("$guidance", terms.Product.Guidance),
            ("$kind", kind), ("$localFirst", localFirst), ("$preorder", preorder),
            ("$deadline", request.Payment == CheckoutPayment.Wallet ? null : dueAt),
            ("$confirmed", request.Payment == CheckoutPayment.Wallet ? now : null),
            ("$now", now),
            ("$percent", terms.DiscountPercent is decimal percentage ? ExactDecimal.Format(percentage) : null),
            ("$discountUnits", terms.DiscountUnits));
        await Change(tx, """
            INSERT INTO bot_activity_events(id,user_id,event_type,source_kind,action_code,product_id,
                order_id,quantity_selected,outcome_code,idempotency_key,occurred_at)
            VALUES ($id,$member,'checkout','flow','checkout_created',$product,$order,$quantity,'accepted',$key,$now)
            """, ct, ("$id", Guid.CreateVersion7()), ("$member", member),
            ("$product", request.ProductId), ("$order", request.OrderId), ("$quantity", request.Quantity),
            ("$key", "workflow:checkout_created:" + request.OrderId.ToString("N")), ("$now", now));
        if (terms.DiscountId is not null)
            await SqliteDiscountClaimStore.HoldAsync(tx, context, member, request.OrderId, request.ProductId,
                quote, ct);
        if (request.Kind == CheckoutKind.PrepaidUnits && request.Payment == CheckoutPayment.Wallet)
        {
            using (var ensure = SqliteWallet.Command(tx,
                "INSERT INTO wallets(user_id) VALUES ($member) ON CONFLICT(user_id) DO NOTHING",
                ("$member", member))) await ensure.ExecuteNonQueryAsync(ct);
            var wallet = await SqliteWallet.ReadAsync(tx, member, ct);
            if (wallet.Balance.Amount < quote.TotalUsd.Amount)
                throw new PricingRejectedException();
            await SqliteWallet.PostAsync(tx, member, wallet,
                new Money(-quote.TotalUsd.Amount, Currency.Usd), request.OrderId, false, now, ct);
            _ = await new SqlitePrepaidSourceStore(sessions, secrets).IssueAsync(tx, context, request.OrderId, ct);
            return new(CheckoutOutcome.Purchased, request.OrderId);
        }
        await CreatePaymentAsync(tx, context, request, quote, ct);
        await OutboxStore.EnqueueSqliteAsync(tx, JobIntent.Order(JobKind.ExpireOrderPayment,
            context.BrandId, request.OrderId), request.OrderId, instant.AddMinutes(30), ct);
        await QueueInstructionAsync(tx, context, member, request.OrderId, "created", ct);
        return new(CheckoutOutcome.AwaitingPayment, request.OrderId);
    }

    private async Task CreatePaymentAsync(SqliteTransaction tx, BrandContext context, CheckoutRequest request,
        CheckoutQuote quote, CancellationToken ct)
    {
        bool bank = request.Payment == CheckoutPayment.Bank;
        Guid payment = Guid.CreateVersion7();
        byte[]? destination = null;
        string? key = null;
        if (bank)
        {
            byte[] number, holder;
            using (var card = SqliteWallet.Command(tx, """
                SELECT bank_number_ciphertext,bank_holder_ciphertext,bank_key_id
                FROM brands WHERE id=$brand AND allow_bank=1
                """, ("$brand", context.BrandId)))
            await using (var row = await card.ExecuteReaderAsync(ct))
            {
                if (!await row.ReadAsync(ct) || row.IsDBNull(0) || row.IsDBNull(1) || row.IsDBNull(2))
                    throw new PricingRejectedException();
                var cipher = new BrandCipher(secrets);
                string cardKey = row.GetString(2);
                number = cipher.Decrypt(context, "brands", context.BrandId,
                    "bank_number_ciphertext", cardKey, row.GetFieldValue<byte[]>(0));
                try
                {
                    holder = cipher.Decrypt(context, "brands", context.BrandId,
                        "bank_holder_ciphertext", cardKey, row.GetFieldValue<byte[]>(1));
                }
                catch { CryptographicOperations.ZeroMemory(number); throw; }
            }
            try
            {
                using var data = new MemoryStream();
                using (var writer = new BinaryWriter(data, Encoding.UTF8, leaveOpen: true))
                {
                    writer.Write(1);
                    writer.Write(number.Length);
                    writer.Write(number);
                    writer.Write(holder.Length);
                    writer.Write(holder);
                }
                try
                {
                    destination = new BrandCipher(secrets).Encrypt(context, "payments", payment,
                        "bank_destination_ciphertext", data.GetBuffer().AsSpan(0, checked((int)data.Length)));
                }
                finally { CryptographicOperations.ZeroMemory(data.GetBuffer()); }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(number);
                CryptographicOperations.ZeroMemory(holder);
            }
            key = secrets.CurrentBrandKeyId(context);
        }
        Money due = bank ? quote.TotalToman ?? throw new PricingRejectedException() : quote.TotalUsd;
        if (due.Amount <= 0) throw new PricingRejectedException();
        await Change(tx, """
            INSERT INTO payments(id,order_id,payment_method,quote_currency,amount_due,idempotency_ref,
                network_asset,bank_destination_ciphertext,bank_destination_key_id)
            VALUES ($id,$order,$method,$currency,$due,$origin,$asset,$cipher,$key)
            """, ct, ("$id", payment), ("$order", request.OrderId),
            ("$method", bank ? "bank" : "crypto"), ("$currency", due.Currency.ToCode()),
            ("$due", ExactDecimal.Format(due.Amount)), ("$origin", "order:" + request.OrderId.ToString("D")),
            ("$asset", bank ? null : request.Network == CryptoAsset.UsdtBsc ? "USDTBSC" : "USDTTRC20"),
            ("$cipher", destination), ("$key", key));
    }

    private static async Task QueueInstructionAsync(SqliteTransaction tx, BrandContext context, Guid member,
        Guid order, string cause, CancellationToken ct)
    {
        Guid delivery = Guid.CreateVersion7();
        string dedupe = "instruction-" + cause + ":" + order.ToString("D");
        using (var command = SqliteWallet.Command(tx, """
            INSERT INTO telegram_deliveries(id,brand_id,recipient_kind,user_id,purpose,action,target_message_id,dedupe_key,order_id)
            SELECT $id,$brand,'customer_dm',$member,'order_status',CASE WHEN a.message IS NULL THEN 'send' ELSE 'edit' END,
                a.message,$dedupe,$order FROM (SELECT 1) seed LEFT JOIN (
                SELECT telegram_message_id message FROM telegram_deliveries WHERE order_id=$order
                    AND purpose='order_status' AND status='succeeded' AND telegram_message_id IS NOT NULL
                ORDER BY created_at,id LIMIT 1) a ON true WHERE true
            ON CONFLICT(dedupe_key) DO NOTHING
            """, ("$id", delivery), ("$brand", context.BrandId), ("$member", member),
            ("$dedupe", dedupe), ("$order", order)))
            await command.ExecuteNonQueryAsync(ct);
        using var lookup = SqliteWallet.Command(tx, """
            SELECT id FROM telegram_deliveries WHERE brand_id=$brand AND order_id=$order AND dedupe_key=$dedupe
            """, ("$brand", context.BrandId), ("$order", order), ("$dedupe", dedupe));
        if (await lookup.ExecuteScalarAsync(ct) is not string value)
            throw new InvalidOperationException("Payment instruction was not persisted.");
        Guid actual = Guid.Parse(value);
        await OutboxStore.EnqueueSqliteAsync(tx, JobIntent.Telegram(context.BrandId, actual), actual,
            DateTime.UtcNow, ct);
    }

    private static byte[] PreparedFingerprint(BrandContext context, Guid member, CheckoutRequest request) =>
        SHA256.HashData(Encoding.ASCII.GetBytes(
            $"prepared-goods-v1:{context.BrandId:D}:{member:D}:{request.ProductId:D}:{request.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{request.Payment}" +
            (request.Kind == CheckoutKind.PrepaidUnits ? ":prepaid" : "") +
            (request.Network is CryptoAsset network ? ":" + network : "")));

    private static async Task Change(SqliteTransaction tx, string sql, CancellationToken ct,
        params (string Name, object? Value)[] values)
    {
        using var command = SqliteWallet.Command(tx, sql, values);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            throw new InvalidOperationException("The prepared checkout write lost its target.");
    }
}
