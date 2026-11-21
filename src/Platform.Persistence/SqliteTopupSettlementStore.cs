using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

public sealed class SqliteTopupSettlementStore(SqliteSessionFactory sessions)
{
    public async Task<PaymentSettlement> SettleAsync(BrandContext context, long expectedRevision, Guid payment,
        Func<InvoiceExpectation, CancellationToken, Task<ProviderPaymentSnapshot>> lookup, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);

        var before = await sessions.RunAsync(false, (_, tx, token) => ReadAsync(tx, context, expectedRevision, payment, token), ct);
        if (before.Invoice is null) return Pending(before);
        var remote = await lookup(before.Invoice, ct);
        return await sessions.RunAsync(true, async (_, tx, token) =>
        {
            var current = await ReadAsync(tx, context, expectedRevision, payment, token);
            if (current.Invoice is null) return Pending(current);
            if (current.Member != before.Member || current.Invoice != before.Invoice || current.Proof is null || before.Proof is null ||
                !CryptographicOperations.FixedTimeEquals(current.Proof, before.Proof) ||
                Stopwatch.GetElapsedTime(remote.ObservedAt) > TimeSpan.FromSeconds(30)) throw new PaymentNoticeRejectedException();

            bool sameInvoice = remote.Invoice == current.Invoice;
            if (!sameInvoice || remote.Received != current.Invoice.ExpectedTokens || remote.Status != "finished")
            {
                if (sameInvoice && remote.Received <= current.Invoice.ExpectedTokens &&
                    remote.Status is ("waiting" or "confirming" or "confirmed" or "sending"))
                    return new PaymentSettlement(PaymentSettlementOutcome.Pending, ExternalSettlementRoute.Review);

                using var review = SqliteWallet.Command(tx, "UPDATE payments SET status='review' WHERE id=$id AND status='confirming'", ("$id", payment));
                if (await review.ExecuteNonQueryAsync(token) != 1) throw new PaymentNoticeRejectedException();
                return new(PaymentSettlementOutcome.Review, ExternalSettlementRoute.Review);
            }

            using var ensure = SqliteWallet.Command(tx, "INSERT INTO wallets(user_id) VALUES ($member) ON CONFLICT(user_id) DO NOTHING", ("$member", current.Member));
            await ensure.ExecuteNonQueryAsync(token);
            var wallet = await SqliteWallet.ReadAsync(tx, current.Member, token);
            Guid credit = await SqliteWallet.PostAsync(tx, current.Member, wallet, ExternalPaymentPolicy.FrozenCredit(current.Invoice.UsdDue),
                payment, true, SqliteSchema.UtcText(DateTime.UtcNow), token);
            using var settle = SqliteWallet.Command(tx, """
                UPDATE payments SET status='settled',compensating_wallet_entry_id=$credit WHERE id=$id AND status='confirming'
                """, ("$credit", credit), ("$id", payment));
            if (await settle.ExecuteNonQueryAsync(token) != 1) throw new PaymentNoticeRejectedException();
            return new(PaymentSettlementOutcome.Settled, ExternalSettlementRoute.Topup, credit);
        }, ct);
    }

    private static async Task<State> ReadAsync(SqliteTransaction tx, BrandContext context, long revision, Guid payment, CancellationToken ct)
    {
        using var command = SqliteWallet.Command(tx, """
            SELECT p.topup_user_id,p.status,p.compensating_wallet_entry_id,p.payment_method,p.provider_code,p.provider_status_code,
                p.provider_payment_ref,p.idempotency_ref,p.network_asset,p.amount_due,p.expected_network_amount,p.network_amount,p.amount_received,
                p.ipn_payload_sha256,p.evidence_asset_ref,p.confirmed_at,p.first_payment_observed_at
            FROM payments p JOIN users u ON u.id=p.topup_user_id JOIN brands b ON b.id=u.brand_id
            WHERE p.id=$id AND b.id=$brand AND b.bot_id=$bot AND b.active AND b.policy_version=$revision AND p.quote_currency='USD'
            """, ("$id", payment), ("$brand", context.BrandId), ("$bot", context.BotId), ("$revision", revision));
        await using var row = await command.ExecuteReaderAsync(ct);
        if (!await row.ReadAsync(ct)) throw new UnauthorizedAccessException("Payment is unavailable.");
        Guid member = row.GetGuid(0);
        string status = row.GetString(1);
        Guid? credit = row.IsDBNull(2) ? null : row.GetGuid(2);
        if (status != "confirming" || row.GetString(3) != "crypto" || Text(row, 4) != "nowpayments" ||
            Text(row, 5) != "finished" || Text(row, 6) is not string provider || Number(row, 10) is not > 0 ||
            Number(row, 10) != Number(row, 11) || Number(row, 9) != Number(row, 12) ||
            row.IsDBNull(13) || row.IsDBNull(14) || row.IsDBNull(15) || row.IsDBNull(16))
            return new(member, status, credit, null, null);

        string network = Text(row, 8) switch { "USDTBSC" => "usdtbsc", "USDTTRC20" => "usdttrc20", _ => throw new PaymentNoticeRejectedException() };
        return new(member, status, credit, new InvoiceExpectation(provider, row.GetString(7), Number(row, 9)!.Value, network, Number(row, 10)!.Value),
            row.GetFieldValue<byte[]>(13));
    }

    private static PaymentSettlement Pending(State state) => state.Status switch
    {
        "settled" when state.Credit.HasValue => new(PaymentSettlementOutcome.Replayed, ExternalSettlementRoute.AlreadyAccepted, state.Credit),
        "settled" => throw new PaymentNoticeRejectedException(),
        "review" => new(PaymentSettlementOutcome.Review, ExternalSettlementRoute.Review),
        _ => new(PaymentSettlementOutcome.Pending, ExternalSettlementRoute.Review)
    };

    private static string? Text(SqliteDataReader row, int i) => row.IsDBNull(i) ? null : row.GetString(i);
    private static decimal? Number(SqliteDataReader row, int i) => row.IsDBNull(i) ? null : ExactDecimal.Parse(row.GetString(i));
    private sealed record State(Guid Member, string Status, Guid? Credit, InvoiceExpectation? Invoice, byte[]? Proof);
}
