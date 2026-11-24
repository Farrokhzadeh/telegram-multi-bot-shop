using Microsoft.EntityFrameworkCore;
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
        return sessions.RunEfAsync(true, null, async (data, token) =>
        {
            var member = (await data.RequireMembershipAsync(context, actor, false, false, token, true))!;
            await data.RequireRegisteredRevisionAsync(context, revision, token);
            var prior = await data.Data.Set<Entities.Payment>().Where(value => value.Id == payment).Select(value => new { value.TopupUserId, value.AmountDue, value.NetworkAsset, value.PaymentMethod, value.QuoteCurrency }).SingleOrDefaultAsync(token);
            if (prior is not null)
            {
                if (prior.TopupUserId != member.Id || prior.AmountDue != amount.Amount || prior.NetworkAsset != asset || prior.PaymentMethod != "crypto" || prior.QuoteCurrency != "USD") data.RejectScope("The top-up identity was already used.");
                return payment;
            }
            var policy = await data.Data.Set<Entities.Brand>().Where(value => value.Id == context.BrandId).Select(value => new { value.AllowCrypto, value.AllowWalletTopup, value.ZeroBalanceTopup }).SingleAsync(token);
            decimal balance = await data.Data.Set<Entities.Wallet>().Where(value => value.UserId == member.Id).Select(value => (decimal?)value.Balance).SingleOrDefaultAsync(token) ?? 0m;
            if (!policy.AllowCrypto || !policy.AllowWalletTopup || (!policy.ZeroBalanceTopup && balance <= 0)) data.RejectScope("Wallet top-up is unavailable.");
            data.Data.Add(new Entities.Payment { Id = payment, TopupUserId = member.Id, PaymentMethod = "crypto", QuoteCurrency = "USD", AmountDue = amount.Amount, IdempotencyRef = "topup:" + payment.ToString("D"), NetworkAsset = asset });
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
        return sessions.RunEfAsync(true, null, async (data, token) =>
        {
            var member = (await data.RequireMembershipAsync(context, actor, false, false, token, true))!;
            await data.RequireRegisteredRevisionAsync(context, revision, token);
            var row = await (from value in data.Data.Set<Entities.Payment>()
                             join order in data.Data.Set<Entities.Order>() on value.OrderId equals order.Id into orders
                             from order in orders.DefaultIfEmpty()
                             where value.Id == payment && (value.TopupUserId ?? (Guid?)order.UserId) == member.Id
                             select value).SingleOrDefaultAsync(token);
            if (row is null || row.PaymentMethod != "crypto" || row.QuoteCurrency != "USD") data.RejectScope("The invoice is unavailable.");
            if (row.ProviderPaymentRef is not null)
            {
                if (row.ProviderPaymentRef != providerPayment || row.ProviderCode != "nowpayments" || row.ExpectedNetworkAmount != expectedTokens) data.RejectScope("The invoice origin has changed.");
                return 0;
            }
            if (row.HostedInvoiceStartedAt != null || row.Status is not ("created" or "awaiting")) data.RejectScope("The invoice needs review.");
            row.ProviderCode = "nowpayments"; row.ProviderPaymentRef = providerPayment; row.ExpectedNetworkAmount = expectedTokens; row.Status = "awaiting";
            return 0;
        }, ct);
    }
}
