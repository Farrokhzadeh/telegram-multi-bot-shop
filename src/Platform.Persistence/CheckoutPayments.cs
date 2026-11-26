using System.Security.Cryptography;
using Platform.Core;

namespace Platform.Persistence;

public sealed partial class CheckoutStore
{
    private async Task CreatePaymentAsync(DbSession session, BrandContext context, CheckoutRequest request, CheckoutQuote quote, CancellationToken ct)
    {
        Guid payment = Guid.CreateVersion7();
        byte[]? destination = null;
        bool bank = quote.Terms.Payment == CheckoutPayment.Bank;
        if (bank)
        {
            var snapshot = await session.RunEfStepAsync(false,
                (data, token) => CheckoutQueries.FreezeBankDestinationAsync(data, context, false, token), ct);
            if (snapshot is null) Reject(session);
            destination = snapshot;
        }
        try
        {
            Money due = bank ? quote.TotalToman ?? throw new PricingRejectedException() : quote.TotalUsd;
            if (due.Amount <= 0) throw new PricingRejectedException();
            await session.RunEfStepAsync(true, async (data, token) =>
            {
                await data.RequireOrderCommandAsync(context, request.OrderId, token);
                data.Data.Add(new Entities.Payment { Id = payment, OrderId = request.OrderId, PaymentMethod = bank ? "bank" : "crypto", QuoteCurrency = due.Currency.ToCode(), AmountDue = due.Amount, IdempotencyRef = "order:" + request.OrderId.ToString("D"), NetworkAsset = bank ? null : request.Network == CryptoAsset.UsdtBsc ? "USDTBSC" : "USDTTRC20", BankDestination = destination });
                return true;
            }, ct);
        }
        finally { if (destination is not null) CryptographicOperations.ZeroMemory(destination); }
    }
}
