using System.Globalization;
using Microsoft.Data.Sqlite;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

internal sealed class SqliteCustomerScreens(SqliteSessionFactory sessions)
{
    internal Task<CustomerHome> HomeAsync(BrandContext context, long actor, CancellationToken ct = default) =>
        sessions.RunAsync(false, async (_, tx, token) =>
        {
            var member = await RequireAsync(tx, context, actor, token);
            string balance = "0";
            using (var command = SqliteWallet.Command(tx, "SELECT balance FROM wallets WHERE user_id=$member", ("$member", member.Id)))
                balance = (string?)await command.ExecuteScalarAsync(token) ?? "0";
            long orders;
            using (var command = SqliteWallet.Command(tx, "SELECT count(*) FROM orders WHERE user_id=$member", ("$member", member.Id)))
                orders = (long)(await command.ExecuteScalarAsync(token))!;
            decimal spent = 0;
            using (var command = SqliteWallet.Command(tx, "SELECT signed_amount FROM wallet_entries WHERE user_id=$member AND movement_kind='purchase_debit'",
                ("$member", member.Id)))
            await using (var row = await command.ExecuteReaderAsync(token))
                while (await row.ReadAsync(token)) spent = checked(spent - ExactDecimal.Parse(row.GetString(0)));
            return new CustomerHome(member.Language, member.Name, ExactDecimal.Parse(balance), orders, spent, Utc(member.Joined), member.Contact);
        }, ct);

    internal Task SetLanguageAsync(BrandContext context, long actor, string language, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(language) || language.Length>35 || language[0]=='-' || language[^1]=='-' ||
            language.Any(c => !char.IsAsciiLetterOrDigit(c) && c!='-')) throw new ArgumentException("Invalid language code.", nameof(language));
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            var member = await RequireAsync(tx, context, actor, token);
            using var command = SqliteWallet.Command(tx,
                "UPDATE users SET language=$language WHERE id=$member AND brand_id=$brand AND telegram_id=$actor AND blocked_at IS NULL AND banned_at IS NULL",
                ("$language", language), ("$member", member.Id), ("$brand", context.BrandId), ("$actor", actor));
            if (await command.ExecuteNonQueryAsync(token)!=1) throw new UnauthorizedAccessException();
            return 0;
        }, ct);
    }

    internal Task<CustomerScreenPage> ListAsync(BrandContext context, long actor, CustomerScreenList screen, string filter, int page,
        CancellationToken ct = default)
    {
        const int size=5;
        ArgumentOutOfRangeException.ThrowIfNegative(page);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(page, int.MaxValue/size);
        if (filter is not ("all" or "active" or "finished")) throw new ArgumentOutOfRangeException(nameof(filter));
        return sessions.RunAsync(false, async (_, tx, token) =>
        {
            var member = await RequireAsync(tx, context, actor, token);
            string sql = screen switch
            {
                CustomerScreenList.Orders => """
                    SELECT id,product_title_snapshot,fulfillment_status,NULL,NULL,created_at,quantity
                    FROM orders WHERE user_id=$member AND ($filter='all' OR
                        ((fulfillment_status IN ('ready','completed','cancelled','compensated')
                            OR payment_status IN ('expired','cancelled'))=($filter='finished')))
                    ORDER BY created_at DESC,id DESC LIMIT 6 OFFSET $offset
                    """,
                CustomerScreenList.Transactions => """
                    SELECT id,movement_kind,'',signed_amount,'USD',created_at,NULL
                    FROM wallet_entries WHERE user_id=$member AND is_posting=1
                    ORDER BY created_at DESC,id DESC LIMIT 6 OFFSET $offset
                    """,
                CustomerScreenList.Deposits => """
                    SELECT p.id,p.payment_method,p.status,p.amount_due,p.quote_currency,p.created_at,NULL
                    FROM payments p LEFT JOIN orders o ON o.id=p.order_id
                    WHERE p.topup_user_id=$member OR o.user_id=$member
                    ORDER BY p.created_at DESC,p.id DESC LIMIT 6 OFFSET $offset
                    """,
                CustomerScreenList.Discounts => """
                    SELECT id,display_prefix,kind,percent_off,'%',created_at,NULL
                    FROM discounts WHERE brand_id=$brand AND eligible_user_id=$member AND active=1
                        AND (starts_at IS NULL OR starts_at<=$now) AND (ends_at IS NULL OR ends_at>$now)
                    ORDER BY created_at DESC,id DESC LIMIT 6 OFFSET $offset
                    """,
                _ => throw new ArgumentOutOfRangeException(nameof(screen))
            };
            using var command = SqliteWallet.Command(tx, sql, ("$member", member.Id), ("$brand", context.BrandId),
                ("$filter", filter), ("$offset", page*size), ("$now", SqliteSchema.UtcText(DateTime.UtcNow)));
            var entries = await ReadAsync(command, 6, token);
            return new CustomerScreenPage(entries.Take(size).ToArray(), entries.Count>size);
        }, ct);
    }

    internal Task<CustomerScreenEntry?> DetailAsync(BrandContext context, long actor, CustomerScreenList screen, Guid id,
        CancellationToken ct = default) =>
        sessions.RunAsync(false, async (_, tx, token) =>
        {
            var member = await RequireAsync(tx, context, actor, token);
            string sql = screen switch
            {
                CustomerScreenList.Orders => """
                    SELECT o.id,o.product_title_snapshot,o.fulfillment_status,
                        (SELECT CASE WHEN o.payment_method='wallet' THEN akanti_decimal_add('0',substr(w.signed_amount,2)) ELSE NULL END
                        FROM wallet_entries w WHERE w.order_id=o.id AND w.user_id=$member AND w.movement_kind='purchase_debit' LIMIT 1),
                        o.currency,o.created_at,o.quantity
                    FROM orders o WHERE o.user_id=$member AND o.id=$id
                    """,
                CustomerScreenList.Deposits => """
                    SELECT p.id,p.payment_method,p.status,p.amount_due,p.quote_currency,p.created_at,NULL
                    FROM payments p LEFT JOIN orders o ON o.id=p.order_id
                    WHERE p.id=$id AND (p.topup_user_id=$member OR o.user_id=$member)
                    """,
                _ => throw new ArgumentOutOfRangeException(nameof(screen))
            };
            using var command = SqliteWallet.Command(tx, sql, ("$member", member.Id), ("$id", id));
            return (await ReadAsync(command, 1, token)).SingleOrDefault();
        }, ct);

    private static async Task<List<CustomerScreenEntry>> ReadAsync(SqliteCommand command, int maximum, CancellationToken ct)
    {
        var entries = new List<CustomerScreenEntry>();
        await using var row = await command.ExecuteReaderAsync(ct);
        while (await row.ReadAsync(ct))
        {
            if (entries.Count==maximum) throw new InvalidOperationException("Customer result limit exceeded.");
            entries.Add(new(row.GetGuid(0), row.GetString(1), row.GetString(2), row.IsDBNull(3) ? null : ExactDecimal.Parse(row.GetString(3)),
                row.IsDBNull(4) ? null : row.GetString(4), Utc(row.GetString(5)), row.IsDBNull(6) ? null : row.GetInt32(6)));
        }
        return entries;
    }

    private static async Task<Member> RequireAsync(SqliteTransaction tx, BrandContext context, long actor, CancellationToken ct)
    {
        if (actor<=0) throw new ArgumentOutOfRangeException(nameof(actor));
        using var command = SqliteWallet.Command(tx, """
            SELECT u.id,u.language,b.public_name,u.first_seen_at,
                (SELECT a.public_contact_handle FROM admins a WHERE a.id=b.customer_support_admin_id
                    AND a.disabled_at IS NULL AND a.telegram_user_id IS NOT NULL
                    AND a.telegram_verified_at IS NOT NULL AND a.telegram_verification_audit_id IS NOT NULL)
            FROM users u JOIN brands b ON b.id=u.brand_id
            WHERE u.brand_id=$brand AND b.bot_id=$bot AND b.active=1 AND u.telegram_id=$actor
                AND u.blocked_at IS NULL AND u.banned_at IS NULL
            """, ("$brand", context.BrandId), ("$bot", context.BotId), ("$actor", actor));
        await using var row = await command.ExecuteReaderAsync(ct);
        if (!await row.ReadAsync(ct)) throw new UnauthorizedAccessException("Customer membership is unavailable.");
        string? contact = row.IsDBNull(4) ? null : row.GetString(4);
        if (contact is not null && (contact.Length is <5 or >32 || !contact.All(c => char.IsAsciiLetterOrDigit(c) || c=='_'))) contact=null;
        string language = row.GetString(1);
        return new(row.GetGuid(0), language, row.GetString(2), row.GetString(3), contact);
    }

    private static DateTime Utc(string text) => DateTime.ParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private sealed record Member(Guid Id, string Language, string Name, string Joined, string? Contact);
}
