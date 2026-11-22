using System.Globalization;
using Microsoft.Data.Sqlite;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

public sealed class SqlitePaymentEvidenceStore(SqliteSessionFactory sessions)
{
    public Task<Guid> CreateTopupAsync(BrandContext context, long revision, long actor, Guid payment,
        Money amount, CryptoAsset network, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (revision < 0 || actor <= 0 || payment == Guid.Empty || amount.Currency != Currency.Usd ||
            amount.Amount <= 0 || decimal.Round(amount.Amount, 2) != amount.Amount || !Enum.IsDefined(network))
            throw new ArgumentException("Invalid SQLite wallet top-up intent.");
        string asset = network == CryptoAsset.UsdtBsc ? "USDTBSC" : "USDTTRC20";
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            var member = await MemberAsync(tx, context, revision, actor, token);
            using (var previous = SqliteWallet.Command(tx, """
                SELECT topup_user_id,amount_due,network_asset,payment_method,quote_currency
                FROM payments WHERE id=$payment
                """, ("$payment", payment)))
            await using (var row = await previous.ExecuteReaderAsync(token))
            {
                if (await row.ReadAsync(token))
                {
                    if (row.IsDBNull(0) || row.GetGuid(0) != member.Id ||
                        ExactDecimal.Parse(row.GetString(1)) != amount.Amount || row.GetString(2) != asset ||
                        row.GetString(3) != "crypto" || row.GetString(4) != "USD")
                        throw new UnauthorizedAccessException("The top-up identity was already used.");
                    return payment;
                }
            }
            using (var policy = SqliteWallet.Command(tx, """
                SELECT b.allow_crypto,b.allow_wallet_topup,b.zero_balance_topup,coalesce(w.balance,'0')
                FROM brands b LEFT JOIN wallets w ON w.user_id=$member
                WHERE b.id=$brand AND b.bot_id=$bot AND b.active=1 AND b.policy_version=$revision
                """, ("$member", member.Id), ("$brand", context.BrandId), ("$bot", context.BotId),
                ("$revision", revision)))
            await using (var row = await policy.ExecuteReaderAsync(token))
            {
                if (!await row.ReadAsync(token) || !row.GetBoolean(0) || !row.GetBoolean(1) ||
                    !row.GetBoolean(2) && ExactDecimal.Parse(row.GetString(3)) <= 0)
                    throw new UnauthorizedAccessException("Wallet top-up is unavailable.");
            }
            using var insert = SqliteWallet.Command(tx, """
                INSERT INTO payments(id,topup_user_id,payment_method,quote_currency,amount_due,idempotency_ref,network_asset)
                VALUES ($id,$member,'crypto','USD',$amount,$origin,$asset)
                """, ("$id", payment), ("$member", member.Id), ("$amount", ExactDecimal.Format(amount.Amount)),
                ("$origin", "topup:" + payment.ToString("D")), ("$asset", asset));
            if (await insert.ExecuteNonQueryAsync(token) != 1)
                throw new InvalidOperationException("Wallet top-up was not persisted.");
            return payment;
        }, ct);
    }

    public Task BindInvoiceAsync(BrandContext context, long revision, long actor, Guid payment,
        string providerPayment, decimal expectedTokens, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (revision < 0 || actor <= 0 || payment == Guid.Empty || providerPayment.Length is < 1 or > 64 ||
            !providerPayment.All(char.IsAsciiDigit) || expectedTokens <= 0)
            throw new ArgumentException("Invalid trusted SQLite invoice binding.");
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            var member = await MemberAsync(tx, context, revision, actor, token);
            using var query = SqliteWallet.Command(tx, """
                SELECT p.provider_code,p.provider_payment_ref,p.expected_network_amount,p.status,
                    p.hosted_invoice_started_at,p.payment_method,p.quote_currency
                FROM payments p LEFT JOIN orders o ON o.id=p.order_id
                JOIN users u ON u.id=coalesce(p.topup_user_id,o.user_id)
                WHERE p.id=$payment AND u.id=$member AND u.brand_id=$brand
                """, ("$payment", payment), ("$member", member.Id), ("$brand", context.BrandId));
            await using var row = await query.ExecuteReaderAsync(token);
            if (!await row.ReadAsync(token) || row.GetString(5) != "crypto" || row.GetString(6) != "USD")
                throw new UnauthorizedAccessException("The invoice is unavailable.");
            string? provider = row.IsDBNull(1) ? null : row.GetString(1);
            if (provider is not null)
            {
                if (provider != providerPayment || row.IsDBNull(0) || row.GetString(0) != "nowpayments" ||
                    row.IsDBNull(2) || ExactDecimal.Parse(row.GetString(2)) != expectedTokens)
                    throw new UnauthorizedAccessException("The invoice origin has changed.");
                return 0;
            }
            if (!row.IsDBNull(4) || row.GetString(3) is not ("created" or "awaiting"))
                throw new UnauthorizedAccessException("The invoice needs review.");
            await row.DisposeAsync();
            using var update = SqliteWallet.Command(tx, """
                UPDATE payments SET provider_code='nowpayments',provider_payment_ref=$provider,
                    expected_network_amount=$amount,status='awaiting'
                WHERE id=$payment AND provider_payment_ref IS NULL AND status IN ('created','awaiting')
                    AND hosted_invoice_started_at IS NULL
                """, ("$provider", providerPayment), ("$amount", ExactDecimal.Format(expectedTokens)),
                ("$payment", payment));
            if (await update.ExecuteNonQueryAsync(token) != 1)
                throw new UnauthorizedAccessException("The invoice origin has changed.");
            return 0;
        }, ct);
    }

    private static async Task<(Guid Id, long Actor)> MemberAsync(SqliteTransaction tx, BrandContext context,
        long revision, long actor, CancellationToken ct)
    {
        using var query = SqliteWallet.Command(tx, """
            SELECT u.id,u.telegram_id FROM users u JOIN brands b ON b.id=u.brand_id
            WHERE b.id=$brand AND b.bot_id=$bot AND b.active=1 AND b.policy_version=$revision
                AND u.telegram_id=$actor AND u.blocked_at IS NULL AND u.banned_at IS NULL
            """, ("$brand", context.BrandId), ("$bot", context.BotId), ("$revision", revision), ("$actor", actor));
        await using var row = await query.ExecuteReaderAsync(ct);
        if (!await row.ReadAsync(ct)) throw new UnauthorizedAccessException("The customer is unavailable.");
        return (row.GetGuid(0), row.GetInt64(1));
    }
}
