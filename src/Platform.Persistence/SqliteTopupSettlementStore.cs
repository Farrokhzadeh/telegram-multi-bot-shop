using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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

                await ChangeAsync(tx, context, payment, "review", null, token);
                return new(PaymentSettlementOutcome.Review, ExternalSettlementRoute.Review);
            }

            var wallet = await SqliteWallet.ReadAsync(tx, current.Member, token);
            Guid credit = await SqliteWallet.PostAsync(tx, current.Member, wallet, ExternalPaymentPolicy.FrozenCredit(current.Invoice.UsdDue),
                payment, true, SqliteSchema.UtcText(DateTime.UtcNow), token);
            await ChangeAsync(tx, context, payment, "settled", credit, token);
            return new(PaymentSettlementOutcome.Settled, ExternalSettlementRoute.Topup, credit);
        }, ct);
    }

    private static async Task<State> ReadAsync(SqliteTransaction tx, BrandContext context, long revision, Guid payment, CancellationToken ct)
    {
        var row = await SqliteSessionFactory.StepEfAsync(tx, false, (data, token) =>
            (from value in data.Data.Set<Entities.Payment>()
             join user in data.Data.Set<Entities.User>() on value.TopupUserId equals user.Id
             join brand in data.Data.Set<Entities.Brand>() on user.BrandId equals brand.Id
             where value.Id == payment && brand.Id == context.BrandId && brand.BotId == context.BotId && brand.Active && brand.PolicyVersion == revision && value.QuoteCurrency == "USD"
             select new { Member = user.Id, value.Status, value.CompensatingWalletEntryId, value.PaymentMethod, value.ProviderCode, value.ProviderStatusCode, value.ProviderPaymentRef, value.ExpectedNetworkAmount, value.NetworkAmount, value.AmountDue, value.AmountReceived, value.IpnPayloadSha256, value.EvidenceAssetRef, value.ConfirmedAt, value.FirstPaymentObservedAt, value.NetworkAsset, value.IdempotencyRef }).SingleOrDefaultAsync(token), ct);
        if (row is null) throw new UnauthorizedAccessException("Payment is unavailable.");
        if (row.Status != "confirming" || row.PaymentMethod != "crypto" || row.ProviderCode != "nowpayments" || row.ProviderStatusCode != "finished" || row.ProviderPaymentRef is not string provider || row.ExpectedNetworkAmount is not > 0 || row.ExpectedNetworkAmount != row.NetworkAmount || row.AmountDue != row.AmountReceived || row.IpnPayloadSha256 == null || row.EvidenceAssetRef == null || row.ConfirmedAt == null || row.FirstPaymentObservedAt == null) return new(row.Member, row.Status, row.CompensatingWalletEntryId, null, null);
        string network = row.NetworkAsset switch { "USDTBSC" => "usdtbsc", "USDTTRC20" => "usdttrc20", _ => throw new PaymentNoticeRejectedException() };
        return new(row.Member, row.Status, row.CompensatingWalletEntryId, new InvoiceExpectation(provider, row.IdempotencyRef, row.AmountDue, network, row.ExpectedNetworkAmount.Value), row.IpnPayloadSha256);
    }
    private static Task<bool> ChangeAsync(SqliteTransaction tx, BrandContext context, Guid payment, string status, Guid? credit, CancellationToken ct) => SqliteSessionFactory.StepEfAsync(tx, true, async (data, token) =>
    {
        await data.RequirePaymentCommandAsync(context, payment, token);
        var row = await data.Data.Set<Entities.Payment>().SingleOrDefaultAsync(value => value.Id == payment && value.Status == "confirming", token);
        if (row is null) throw new PaymentNoticeRejectedException();
        row.Status = status;
        if (credit is Guid entry) row.CompensatingWalletEntryId = entry;
        return true;
    }, ct);

    private static PaymentSettlement Pending(State state) => state.Status switch
    {
        "settled" when state.Credit.HasValue => new(PaymentSettlementOutcome.Replayed, ExternalSettlementRoute.AlreadyAccepted, state.Credit),
        "settled" => throw new PaymentNoticeRejectedException(),
        "review" => new(PaymentSettlementOutcome.Review, ExternalSettlementRoute.Review),
        _ => new(PaymentSettlementOutcome.Pending, ExternalSettlementRoute.Review)
    };

    private sealed record State(Guid Member, string Status, Guid? Credit, InvoiceExpectation? Invoice, byte[]? Proof);
}
