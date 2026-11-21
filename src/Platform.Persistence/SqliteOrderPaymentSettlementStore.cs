using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

internal sealed class SqliteOrderPaymentSettlementStore(SqliteSessionFactory sessions, SecretSnapshot secrets)
{
    internal async Task<PaymentSettlement> SettleAsync(BrandContext context, long expectedRevision, Guid payment,
        Func<InvoiceExpectation, CancellationToken, Task<ProviderPaymentSnapshot>> lookup, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        var before = await sessions.RunAsync(false, (_, tx, token) => LoadAsync(tx, context, expectedRevision, payment, token), ct);
        if (before.Status == "settled") return Replayed(before);
        if (!Ready(before)) return Incomplete(before);
        if (before.Kind != "product_purchase" || before.Fulfillment is not ("local" or "manual"))
            throw new NotSupportedException("This SQLite settlement route has not been qualified.");
        var invoice = Expectation(before);
        ProviderPaymentSnapshot remote = await lookup(invoice, ct);
        return await sessions.RunAsync(true, async (_, tx, token) =>
        {
            var current = await LoadAsync(tx, context, expectedRevision, payment, token);
            if (current.Status == "settled") return Replayed(current);
            if (!Ready(current)) return Incomplete(current);
            var unchanged = before with { Hash = current.Hash };
            if (current != unchanged || current.Hash is null || before.Hash is null ||
                !CryptographicOperations.FixedTimeEquals(current.Hash, before.Hash) ||
                Stopwatch.GetElapsedTime(remote.ObservedAt) > TimeSpan.FromSeconds(30))
                throw new PaymentNoticeRejectedException();
            bool sameInvoice = remote.Invoice == invoice;
            if (!sameInvoice || remote.Received != invoice.ExpectedTokens || remote.Status != "finished")
            {
                if (sameInvoice && remote.Received <= invoice.ExpectedTokens &&
                    remote.Status is ("waiting" or "confirming" or "confirmed" or "sending"))
                    return new PaymentSettlement(PaymentSettlementOutcome.Pending, ExternalSettlementRoute.Review);
                await Change(tx, "UPDATE payments SET status='review' WHERE id=$id AND status='confirming'", token, ("$id", payment));
                return new(PaymentSettlementOutcome.Review, ExternalSettlementRoute.Review);
            }

            string now = SqliteSchema.UtcText(DateTime.UtcNow);
            await Change(tx, "UPDATE payments SET status='settled' WHERE id=$id AND status='confirming'", token, ("$id", payment));
            Guid? credit = null;
            ExternalSettlementRoute route;
            bool late = current.OrderStatus is ("expired" or "cancelled");
            if (!late && current.First is not null && current.Deadline is not null &&
                string.CompareOrdinal(current.First, current.Deadline) > 0 &&
                current.OrderStatus is ("awaiting_payment" or "under_review" or "confirmation_pending"))
            {
                await Change(tx, """
                    UPDATE orders SET payment_status='expired',fulfillment_status='cancelled',resolution_kind='unpaid_expiry',
                        resolution_at=$now,revision=revision+1,updated_at=$now WHERE id=$order
                    """, token, ("$now", now), ("$order", current.Order));
                late = true;
            }
            if (late)
            {
                route = ExternalSettlementRoute.LateCredit;
                credit = await CreditAsync(tx, current, "late_crypto_credit", now, token);
            }
            else if (current.OrderStatus is ("awaiting_payment" or "under_review" or "confirmation_pending"))
            {
                await Change(tx, """
                    UPDATE orders SET payment_status='paid',payment_confirmed_at=$confirmed,
                        revision=revision+1,updated_at=$now WHERE id=$order
                    """, token, ("$confirmed", current.Confirmed), ("$now", now), ("$order", current.Order));
                if (current.Preorder)
                {
                    route = ExternalSettlementRoute.LocalPreorder;
                    await FulfillmentAsync(tx, current.Order, "waiting_stock", null, now, token);
                    await OutboxStore.EnqueueSqliteAsync(tx, JobIntent.Product(JobKind.DrainLocalPreorders, current.Product),
                        current.Order, DateTime.UtcNow, token);
                }
                else if (current.Fulfillment == "manual")
                {
                    route = ExternalSettlementRoute.LocalGoods;
                    await FulfillmentAsync(tx, current.Order, "requires_admin", null, now, token);
                }
                else
                {
                    var stock = await ReadStockAsync(tx, current.Product, current.Quantity, token);
                    if (stock.Count != current.Quantity)
                    {
                        route = ExternalSettlementRoute.LocalShortageCredit;
                        credit = await CreditAsync(tx, current, "immediate_crypto_shortage_credit", now, token);
                        await Change(tx, """
                            UPDATE orders SET fulfillment_status='compensated',resolution_kind='immediate_crypto_shortage_credit',
                                resolution_at=$now,resolution_wallet_entry_id=$credit,revision=revision+1,updated_at=$now
                            WHERE id=$order
                            """, token, ("$now", now), ("$credit", credit), ("$order", current.Order));
                    }
                    else
                    {
                        route = ExternalSettlementRoute.LocalGoods;
                        await AcquireAsync(tx, context, current.Order, stock, now, token);
                        await FulfillmentAsync(tx, current.Order, "acquiring", null, now, token);
                        await OutboxStore.EnqueueSqliteAsync(tx,
                            JobIntent.Order(JobKind.PrepareManifest, context.BrandId, current.Order), current.Order, DateTime.UtcNow, token);
                    }
                }
            }
            else throw new PaymentNoticeRejectedException();

            await Change(tx, "UPDATE jobs SET status='cancelled',completed_at=$now WHERE order_id=$order AND job_kind='expire_order_payment' AND status IN ('pending','retry')",
                token, false, ("$now", now), ("$order", current.Order));
            if (credit is Guid entry)
                await Change(tx, "UPDATE payments SET compensating_wallet_entry_id=$credit WHERE id=$payment", token,
                    ("$credit", entry), ("$payment", payment));
            await QueueStatusAsync(tx, context, current, token);
            return new(PaymentSettlementOutcome.Settled, route, credit);
        }, ct);
    }

    private static async Task<State> LoadAsync(SqliteTransaction tx, BrandContext context, long revision, Guid payment, CancellationToken ct)
    {
        using var command = SqliteWallet.Command(tx, """
            SELECT o.user_id,o.id,o.product_id,p.status,o.payment_status,o.fulfillment_kind_snapshot,o.is_preorder,
                o.quantity,o.order_kind,p.payment_method,p.amount_due,p.expected_network_amount,p.network_amount,p.amount_received,
                p.provider_payment_ref,p.idempotency_ref,p.network_asset,p.provider_status_code,p.provider_code,
                p.ipn_payload_sha256,p.evidence_asset_ref,p.confirmed_at,p.first_payment_observed_at,o.payment_due_at,
                p.compensating_wallet_entry_id,p.id
            FROM payments p JOIN orders o ON o.id=p.order_id JOIN users u ON u.id=o.user_id
            JOIN brands b ON b.id=u.brand_id
            WHERE p.id=$payment AND b.id=$brand AND b.bot_id=$bot AND b.active=1 AND b.policy_version=$revision
                AND p.quote_currency='USD'
            """, ("$payment", payment), ("$brand", context.BrandId), ("$bot", context.BotId), ("$revision", revision));
        await using var row = await command.ExecuteReaderAsync(ct);
        if (!await row.ReadAsync(ct)) throw new UnauthorizedAccessException("Payment is unavailable.");
        return new(row.GetGuid(0), row.GetGuid(1), row.GetGuid(2), row.GetString(3), row.GetString(4), row.GetString(5),
            row.GetBoolean(6), row.GetInt32(7), row.GetString(8), row.GetString(9), ExactDecimal.Parse(row.GetString(10)),
            Number(row, 11), Number(row, 12), Number(row, 13), Text(row, 14), row.GetString(15), Text(row, 16), Text(row, 17),
            Text(row, 18), row.IsDBNull(19) ? null : row.GetFieldValue<byte[]>(19), !row.IsDBNull(20),
            Text(row, 21), Text(row, 22), Text(row, 23), row.IsDBNull(24) ? null : row.GetGuid(24), row.GetGuid(25));
    }

    private static bool Ready(State state) =>
        state.Method == "crypto" && state.Status == "confirming" && state.ProviderCode == "nowpayments" &&
        state.Provider is not null && state.ProviderStatus == "finished" && state.Expected is > 0 &&
        state.Tokens == state.Expected && state.Received == state.Due && state.Hash is not null &&
        state.Evidence && state.Confirmed is not null && state.First is not null &&
        state.Asset is ("USDTBSC" or "USDTTRC20");

    private static InvoiceExpectation Expectation(State data) =>
        new(data.Provider!, data.Origin, data.Due, data.Asset == "USDTBSC" ? "usdtbsc" : "usdttrc20", data.Expected!.Value);

    private static PaymentSettlement Incomplete(State state) => new(
        state.Status == "review" ? PaymentSettlementOutcome.Review : PaymentSettlementOutcome.Pending, ExternalSettlementRoute.Review);
    private static PaymentSettlement Replayed(State state) =>
        new(PaymentSettlementOutcome.Replayed, ExternalSettlementRoute.AlreadyAccepted, state.Credit);

    private static async Task<Guid> CreditAsync(SqliteTransaction tx, State state, string kind, string now, CancellationToken ct)
    {
        using (var ensure = SqliteWallet.Command(tx, "INSERT INTO wallets(user_id) VALUES ($member) ON CONFLICT(user_id) DO NOTHING",
            ("$member", state.Member))) await ensure.ExecuteNonQueryAsync(ct);
        var wallet = await SqliteWallet.ReadAsync(tx, state.Member, ct);
        Money increase = ExternalPaymentPolicy.FrozenCredit(state.Due);
        Guid entry = Guid.CreateVersion7();
        using (var command = SqliteWallet.Command(tx, """
            UPDATE wallets SET balance=$new,version=version+1,updated_at=$now WHERE user_id=$member AND version=$version
            """, ("$new", ExactDecimal.Format(wallet.Balance.Add(increase).Amount)), ("$now", now),
            ("$member", state.Member), ("$version", wallet.Version)))
            if (await command.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Wallet version lost.");
        await Change(tx, """
            INSERT INTO wallet_entries(id,user_id,signed_amount,balance_after,posting_version,movement_kind,is_posting,
                idempotency_key,order_id,payment_id,reason)
            VALUES ($entry,$member,$amount,$new,$version,$kind,1,$key,$order,$payment,'verified_original_invoice')
            """, ct, ("$entry", entry), ("$member", state.Member), ("$amount", ExactDecimal.Format(increase.Amount)),
            ("$new", ExactDecimal.Format(wallet.Balance.Add(increase).Amount)), ("$version", checked(wallet.Version + 1)),
            ("$kind", kind), ("$key", "payment-credit:" + state.PaymentId.ToString("D")), ("$order", state.Order),
            ("$payment", state.PaymentId));
        return entry;
    }

    private async Task AcquireAsync(SqliteTransaction tx, BrandContext context, Guid order,
        IReadOnlyList<Stock> stock, string now, CancellationToken ct)
    {
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
            await Change(tx, """
                INSERT INTO fulfillments(id,order_id,unit_no,kind) VALUES ($id,$order,$unit,'local')
                """, ct, ("$id", fulfillment), ("$order", order), ("$unit", ++unit));
            await Change(tx, """
                UPDATE inventory_items SET state='allocated',assigned_fulfillment_id=$id,allocated_at=$now
                WHERE id=$stock AND state='available'
                """, ct, ("$id", fulfillment), ("$now", now), ("$stock", item.Id));
            await Change(tx, """
                UPDATE fulfillments SET status='acquired',delivered_content=$content,delivered_content_key_id=$key,content_kind='text'
                WHERE id=$id
                """, ct, ("$content", encrypted), ("$key", secrets.CurrentBrandKeyId(context)), ("$id", fulfillment));
        }
    }

    private static async Task<List<Stock>> ReadStockAsync(SqliteTransaction tx, Guid product, int quantity, CancellationToken ct)
    {
        var stock = new List<Stock>();
        using var command = SqliteWallet.Command(tx, """
            SELECT id,credential_ciphertext,credential_key_id FROM inventory_items
            WHERE product_id=$product AND state='available' ORDER BY id LIMIT $count
            """, ("$product", product), ("$count", quantity));
        await using var row = await command.ExecuteReaderAsync(ct);
        while (await row.ReadAsync(ct)) stock.Add(new(row.GetGuid(0), row.GetFieldValue<byte[]>(1), row.GetString(2)));
        return stock;
    }

    private static Task FulfillmentAsync(SqliteTransaction tx, Guid order, string status, string? review, string now,
        CancellationToken ct) => Change(tx, """
            UPDATE orders SET fulfillment_status=$status,review_code=$review,revision=revision+1,updated_at=$now WHERE id=$order
            """, ct, ("$status", status), ("$review", review), ("$now", now), ("$order", order));

    private static async Task QueueStatusAsync(SqliteTransaction tx, BrandContext context, State state, CancellationToken ct)
    {
        Guid? anchor = null;
        using (var lookup = SqliteWallet.Command(tx, """
            SELECT id FROM telegram_deliveries WHERE order_id=$order AND purpose='order_status' AND status='succeeded'
            ORDER BY created_at,id LIMIT 1
            """, ("$order", state.Order)))
        {
            var scalar = await lookup.ExecuteScalarAsync(ct);
            if (scalar is string value) anchor = Guid.Parse(value);
        }
        Guid delivery = Guid.CreateVersion7();
        await Change(tx, """
            INSERT INTO telegram_deliveries(id,brand_id,recipient_kind,user_id,purpose,action,target_message_id,dedupe_key,order_id)
            VALUES ($id,$brand,'customer_dm',$member,'order_status',CASE WHEN $anchor IS NULL THEN 'send' ELSE 'edit' END,
                (SELECT telegram_message_id FROM telegram_deliveries WHERE id=$anchor),$key,$order)
            """, ct, ("$id", delivery), ("$brand", context.BrandId), ("$member", state.Member),
            ("$anchor", anchor), ("$key", "payment-status:" + state.PaymentId.ToString("D")), ("$order", state.Order));
        await OutboxStore.EnqueueSqliteAsync(tx, JobIntent.Telegram(context.BrandId, delivery), state.PaymentId, DateTime.UtcNow, ct);
    }

    private static async Task Change(SqliteTransaction tx, string sql, CancellationToken ct,
        params (string Name, object? Value)[] values) => await Change(tx, sql, ct, true, values);
    private static async Task Change(SqliteTransaction tx, string sql, CancellationToken ct, bool exactlyOne,
        params (string Name, object? Value)[] values)
    {
        using var command = SqliteWallet.Command(tx, sql, values);
        int changed = await command.ExecuteNonQueryAsync(ct);
        if (exactlyOne && changed != 1) throw new PaymentNoticeRejectedException();
    }

    private static string? Text(SqliteDataReader row, int index) => row.IsDBNull(index) ? null : row.GetString(index);
    private static decimal? Number(SqliteDataReader row, int index) => row.IsDBNull(index) ? null : ExactDecimal.Parse(row.GetString(index));

    private sealed record Stock(Guid Id, byte[] Content, string Key);
    private sealed record State(Guid Member, Guid Order, Guid Product, string Status, string OrderStatus, string Fulfillment,
        bool Preorder, int Quantity, string Kind, string Method, decimal Due, decimal? Expected, decimal? Tokens,
        decimal? Received, string? Provider, string Origin, string? Asset, string? ProviderStatus, string? ProviderCode,
        byte[]? Hash, bool Evidence, string? Confirmed, string? First, string? Deadline, Guid? Credit, Guid PaymentId);
}
