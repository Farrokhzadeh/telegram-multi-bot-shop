using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

public sealed class SqliteCatalogStore(SqliteSessionFactory sessions, SecretSnapshot secrets)
{
    public Task<CatalogItem?> GetAsync(BrandContext context, long actor, Guid product, CancellationToken ct = default) =>
        sessions.RunAsync(false, async (_, tx, token) =>
        {
            var state = await ReadAsync(tx, context, actor, product, token);
            if (state is null) return null;
            var quote = CheckoutQuote.Create(state.Prices, state.Presentation, 1, CheckoutPayment.Wallet, [], DateTime.UtcNow);
            return new CatalogItem(product, state.Presentation, state.Prices.UnitUsdt, quote.Terms.UnitToman,
                state.Prices.AutoToman, Supply(state, state.Minimum) is null ? CatalogAvailability.OutOfStock :
                CatalogAvailability.Available, state.Minimum, state.Maximum, state.Wallet, state.Crypto,
                state.Bank && state.Market == "iran")
            { LocalStock = state.Local };
        }, ct);

    public async Task<CheckoutQuote> QuoteAsync(BrandContext context, long actor, CatalogQuery query,
        CancellationToken ct = default) => (await PrepareAsync(context, actor, query, ct)).Quote;

    public Task<PreparedCheckout> PrepareAsync(BrandContext context, long actor, CatalogQuery query, CancellationToken ct = default)
    {
        if (actor <= 0 || query.ProductId == Guid.Empty || query.Quantity is < 1 or > 10000 ||
            query.Payment is not (CheckoutPayment.Wallet or CheckoutPayment.Crypto or CheckoutPayment.Bank) ||
            !Enum.IsDefined(query.Kind)) throw new PricingRejectedException();
        return sessions.RunAsync(false, async (_, tx, token) =>
        {
            var state = await ReadAsync(tx, context, actor, query.ProductId, token);
            var supply = state is null ? null : Supply(state, query.Quantity, query.Kind);
            if (state is null || supply is null || query.Quantity < state.Minimum || query.Quantity > state.Maximum ||
                !Allowed(state, query.Payment)) throw new PricingRejectedException();
            var coupons = await CouponsAsync(tx, context, state, token);
            var quote = CheckoutQuote.Create(state.Prices, state.Presentation, query.Quantity, query.Payment,
                coupons, DateTime.UtcNow);
            return new PreparedCheckout(this, context, actor, state.Member, query, quote, supply, state.PolicyRevision,
                state.ProductRevision, state.ListingRevision, null, null, null);
        }, ct);
    }

    public async Task<LocalCheckoutRequest> RequireLocalAsync(BrandContext context, long actor,
        PreparedCheckout prepared, Guid order, CancellationToken ct = default)
    {
        if (order == Guid.Empty || !ReferenceEquals(prepared.Owner, this) || prepared.Context != context ||
            prepared.Actor != actor || Interlocked.Exchange(ref prepared.Used, 1) != 0 ||
            Stopwatch.GetElapsedTime(prepared.IssuedAt) > TimeSpan.FromSeconds(30) ||
            prepared.Query.Payment != CheckoutPayment.Wallet || prepared.Query.Kind != CheckoutKind.ProductPurchase ||
            prepared.Supply.RemoteUnits != 0 || prepared.Supply.WaitingForLocalStock ||
            prepared.Quote.Terms.DiscountId is not null)
            throw new PricingRejectedException();
        return await sessions.RunAsync(false, async (_, tx, token) =>
        {
            var state = await ReadAsync(tx, context, actor, prepared.Query.ProductId, token);
            var supply = state is null ? null : Supply(state, prepared.Query.Quantity, prepared.Query.Kind);
            if (state is null || state.Member != prepared.Member || state.PolicyRevision != prepared.PolicyRevision ||
                state.ProductRevision != prepared.ProductRevision || state.ListingRevision != prepared.ListingRevision ||
                supply != prepared.Supply || Stopwatch.GetElapsedTime(prepared.IssuedAt) > TimeSpan.FromSeconds(30) ||
                (await CouponsAsync(tx, context, state, token)).Count > 0) throw new PricingRejectedException();
            var current = CheckoutQuote.Create(state.Prices, state.Presentation, prepared.Query.Quantity,
                prepared.Query.Payment, [], DateTime.UtcNow);
            if (current.Terms != prepared.Quote.Terms) throw new PricingRejectedException();
            return new LocalCheckoutRequest(order, prepared.Query.ProductId, prepared.Query.Quantity,
                prepared.Quote.Terms.UnitUsdt, prepared.ListingRevision, prepared.ProductRevision, prepared.PolicyRevision);
        }, ct);
    }

    private async Task<Data?> ReadAsync(SqliteTransaction tx, BrandContext context, long actor, Guid product,
        CancellationToken ct)
    {
        using var query = SqliteWallet.Command(tx, """
            SELECT u.id,b.public_name,b.market,b.policy_version,b.allow_wallet,b.allow_crypto,b.allow_bank,
                b.fx_enabled,b.current_fx,b.fx_source,b.fx_fetched_at,b.max_exchange_rate_age_seconds,
                p.fulfillment_kind,p.availability_policy,p.local_first,p.revision,
                l.revision,l.price_usdt,l.price_toman,l.auto_price_toman,l.allow_wallet,l.allow_crypto,
                l.allow_bank,l.min_quantity,l.max_quantity,
                CASE WHEN l.title_mode='override' THEN l.title_override ELSE p.title END,
                CASE l.description_mode WHEN 'override' THEN l.description_override
                    WHEN 'clear' THEN NULL ELSE p.description END,
                CASE l.icon_mode WHEN 'override' THEN l.icon_override WHEN 'clear' THEN NULL ELSE p.icon_ref END,
                CASE l.guidance_mode WHEN 'override' THEN l.guidance_override WHEN 'clear' THEN NULL ELSE p.guidance END
            FROM users u JOIN brands b ON b.id=u.brand_id
            JOIN brand_products l ON l.brand_id=b.id AND l.product_id=$product
            JOIN products p ON p.id=l.product_id
            WHERE u.brand_id=$brand AND u.telegram_id=$actor AND b.bot_id=$bot AND b.active=1
                AND u.blocked_at IS NULL AND u.banned_at IS NULL
                AND p.active=1 AND l.visible=1 AND b.require_channel_join=0
            """, ("$brand", context.BrandId), ("$bot", context.BotId), ("$actor", actor), ("$product", product));
        Guid member;
        string name, market, kind, policy;
        long policyRevision, productRevision, listingRevision;
        bool wallet, crypto, bank, localFirst;
        int minimum, maximum;
        QuotePrices prices;
        ProductPresentation presentation;
        await using (var row = await query.ExecuteReaderAsync(ct))
        {
            if (!await row.ReadAsync(ct)) return null;
            member = row.GetGuid(0);
            name = row.GetString(1);
            market = row.GetString(2);
            policyRevision = row.GetInt64(3);
            wallet = row.GetBoolean(4) && row.GetBoolean(20);
            crypto = row.GetBoolean(5) && row.GetBoolean(21);
            bank = row.GetBoolean(6) && row.GetBoolean(22);
            FxRate? rate = row.IsDBNull(8) || row.IsDBNull(9) || row.IsDBNull(10) ? null :
                new FxRate(ExactDecimal.Parse(row.GetString(8)), row.GetString(9), Utc(row.GetString(10)));
            prices = new(ExactDecimal.Parse(row.GetString(17)),
                row.IsDBNull(18) ? null : ExactDecimal.Parse(row.GetString(18)), row.GetBoolean(19),
                row.GetBoolean(7), rate, TimeSpan.FromSeconds(row.GetInt64(11)));
            kind = row.GetString(12);
            policy = row.GetString(13);
            localFirst = row.GetBoolean(14);
            productRevision = row.GetInt64(15);
            listingRevision = row.GetInt64(16);
            minimum = row.GetInt32(23);
            maximum = row.GetInt32(24);
            presentation = new(name, row.GetString(25), Text(row, 26), Text(row, 27), Text(row, 28));
        }
        long local;
        using (var stock = SqliteWallet.Command(tx, """
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM orders WHERE product_id=$product AND is_preorder=1
                    AND payment_status IN ('paid','not_required')
                    AND fulfillment_status IN ('waiting_stock','requires_admin')
            ) THEN 0 ELSE (
                SELECT count(*) FROM inventory_items WHERE product_id=$product AND state='available'
            ) END
            """, ("$product", product)))
            local = (long)(await stock.ExecuteScalarAsync(ct))!;
        return new(member, product, kind, policy, localFirst, policyRevision, productRevision,
            listingRevision, prices, presentation, wallet, crypto, bank, market, minimum, maximum, local);
    }

    private async Task<IReadOnlyList<QuoteCoupon>> CouponsAsync(SqliteTransaction tx, BrandContext context,
        Data data, CancellationToken ct)
    {
        var result = new List<QuoteCoupon>();
        var cipher = new BrandCipher(secrets);
        string now = SqliteSchema.UtcText(DateTime.UtcNow);
        using var query = SqliteWallet.Command(tx, """
            SELECT d.id,r.id,d.code_ciphertext,d.code_key_id,d.percent_off,d.max_total_uses,
                d.redeemed_count,d.usage_adjustment_units,d.max_uses_per_user,
                (SELECT coalesce(sum(u.units_covered),0) FROM discount_uses u
                    WHERE u.discount_id=d.id AND u.status='held'),
                (SELECT coalesce(sum(u.units_covered),0) FROM discount_uses u
                    JOIN orders o ON o.id=u.order_id WHERE u.discount_id=d.id
                    AND o.user_id=$member AND u.status IN ('held','redeemed')),
                (SELECT coalesce(sum(u.units_covered),0) FROM discount_uses u
                    JOIN orders o ON o.id=u.order_id WHERE u.discount_id=d.id AND o.user_id=$member
                    AND u.status='held' AND o.payment_status='awaiting_payment'
                    AND NOT EXISTS(SELECT 1 FROM payments p WHERE p.order_id=o.id
                        AND (p.first_payment_observed_at IS NOT NULL OR p.receipt_asset_ref IS NOT NULL
                            OR p.status IN ('observed','confirming','settled','review'))))
            FROM discounts d JOIN discount_revisions r ON r.discount_id=d.id AND r.revision_no=d.revision
            WHERE d.brand_id=$brand AND d.active=1
                AND (d.product_id IS NULL OR d.product_id=$product)
                AND (d.eligible_user_id IS NULL OR d.eligible_user_id=$member)
                AND (d.starts_at IS NULL OR d.starts_at<=$now)
                AND (d.ends_at IS NULL OR d.ends_at>$now)
                AND (d.kind<>'prepaid_entitlement' OR EXISTS (
                    SELECT 1 FROM orders s WHERE s.id=d.source_order_id AND s.user_id=$member
                        AND s.product_id=$product AND s.order_kind='prepaid_units' AND s.payment_status='paid'))
            ORDER BY d.id
            """, ("$brand", context.BrandId), ("$product", data.Product), ("$member", data.Member), ("$now", now));
        await using var row = await query.ExecuteReaderAsync(ct);
        while (await row.ReadAsync(ct))
        {
            if (result.Count == 1000) throw new PricingRejectedException();
            long redeemed = row.GetInt64(6), adjustment = row.GetInt64(7), held = row.GetInt64(9), memberUsed = row.GetInt64(10),
                reusable = row.GetInt64(11);
            BigInteger? capacity = row.IsDBNull(5) ? null :
                BigInteger.Max(0, (BigInteger)row.GetInt64(5) + adjustment - redeemed - held + reusable);
            if (!row.IsDBNull(8))
            {
                var own = BigInteger.Max(0, (BigInteger)row.GetInt64(8) - memberUsed + reusable);
                capacity = capacity is BigInteger remaining ? BigInteger.Min(remaining, own) : own;
            }
            Guid id = row.GetGuid(0), revision = row.GetGuid(1);
            byte[] plain = cipher.Decrypt(context, "discounts", id, "code_ciphertext", row.GetString(3),
                row.GetFieldValue<byte[]>(2));
            try
            {
                result.Add(new(id, revision, new UTF8Encoding(false, true).GetString(plain),
                    ExactDecimal.Parse(row.GetString(4)), capacity));
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        return result;
    }

    private static CheckoutSupply? Supply(Data data, int quantity, CheckoutKind kind = CheckoutKind.ProductPurchase)
    {
        if (kind == CheckoutKind.PrepaidUnits) return new(0, 0, false, "prepaid");
        if (data.Policy == "local_preorder") return new(data.Local >= quantity ? quantity : 0, 0,
            data.Local < quantity, "local");
        if (data.Kind == "manual") return new(0, 0, false, "manual");
        int local = data.Kind == "local" || data.LocalFirst ? (int)Math.Min(data.Local, quantity) : 0;
        if (local == quantity) return new(local, 0, false, "local");
        return null;
    }
    private static bool Allowed(Data data, CheckoutPayment payment) => payment switch
    {
        CheckoutPayment.Wallet => data.Wallet,
        CheckoutPayment.Crypto => data.Crypto,
        CheckoutPayment.Bank => data.Bank && data.Market == "iran",
        _ => false
    };
    private static DateTime Utc(string text) => DateTime.ParseExact(text,
        "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    private static string? Text(SqliteDataReader row, int index) => row.IsDBNull(index) ? null : row.GetString(index);
    private sealed record Data(Guid Member, Guid Product, string Kind, string Policy, bool LocalFirst, long PolicyRevision,
        long ProductRevision, long ListingRevision, QuotePrices Prices, ProductPresentation Presentation,
        bool Wallet, bool Crypto, bool Bank, string Market, int Minimum, int Maximum, long Local);
}
