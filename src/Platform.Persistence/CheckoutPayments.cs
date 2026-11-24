using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

public sealed partial class CheckoutStore
{
    private async Task CreatePaymentAsync(DbSession session, BrandContext context, CheckoutRequest request, CheckoutQuote quote, CancellationToken ct)
    {
        Guid payment = Guid.CreateVersion7();
        byte[]? destination = null;
        string? key = null;
        bool bank = quote.Terms.Payment == CheckoutPayment.Bank;
        if (bank)
        {
            var card = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.Brand>().Where(value => value.Id == context.BrandId && value.BankNumberCiphertext != null && value.BankHolderCiphertext != null && value.BankKeyId != null).Select(value => new SavedCard(value.BankNumberCiphertext!, value.BankHolderCiphertext!, value.BankKeyId!)).SingleOrDefaultAsync(token), ct);
            if (card is null) Reject(session);
            var cipher = new BrandCipher(secrets);
            byte[] number = cipher.Decrypt(context, "brands", context.BrandId, "bank_number_ciphertext", card.Key, card.Number);
            byte[]? holder = null;
            try
            {
                holder = cipher.Decrypt(context, "brands", context.BrandId, "bank_holder_ciphertext", card.Key, card.Holder);
                using var bytes = new MemoryStream();
                try
                {
                    using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
                    { writer.Write(1); writer.Write(number.Length); writer.Write(number); writer.Write(holder.Length); writer.Write(holder); }
                    destination = cipher.Encrypt(context, "payments", payment, "bank_destination_ciphertext", bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)));
                }
                finally { CryptographicOperations.ZeroMemory(bytes.GetBuffer()); }
            }
            finally { CryptographicOperations.ZeroMemory(number); if (holder is not null) CryptographicOperations.ZeroMemory(holder); }
            key = secrets.CurrentBrandKeyId(context);
        }
        Money due = bank ? quote.TotalToman ?? throw new PricingRejectedException() : quote.TotalUsd;
        if (due.Amount <= 0) throw new PricingRejectedException();
        await session.RunEfStepAsync(true, async (data, token) =>
        {
            await data.RequireOrderCommandAsync(context, request.OrderId, token);
            data.Data.Add(new Entities.Payment { Id = payment, OrderId = request.OrderId, PaymentMethod = bank ? "bank" : "crypto", QuoteCurrency = due.Currency.ToCode(), AmountDue = due.Amount, IdempotencyRef = "order:" + request.OrderId.ToString("D"), NetworkAsset = bank ? null : request.Network == CryptoAsset.UsdtBsc ? "USDTBSC" : "USDTTRC20", BankDestinationCiphertext = destination, BankDestinationKeyId = key });
            return true;
        }, ct);
    }

    private sealed record SavedCard(byte[] Number, byte[] Holder, string Key);
}
