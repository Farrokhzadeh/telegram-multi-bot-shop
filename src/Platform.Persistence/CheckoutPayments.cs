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
            var card = await session.QuerySingleAsync(new DbStatement("SELECT bank_number_ciphertext,bank_holder_ciphertext,bank_key_id FROM shop.brands WHERE id=$1", DbValue.Uuid(context.BrandId)),
                row => row.IsDBNull(0) || row.IsDBNull(1) || row.IsDBNull(2) ? null : new SavedCard(row.GetBinary(0), row.GetBinary(1), row.GetString(2)), ct);
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
        await session.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.payments(id,order_id,payment_method,quote_currency,amount_due,idempotency_ref,network_asset,bank_destination_ciphertext,bank_destination_key_id)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9)
            """, DbValue.Uuid(payment), DbValue.Uuid(request.OrderId), DbValue.Text(bank ? "bank" : "crypto"), DbValue.Currency(due.Currency), DbValue.Numeric(due.Amount),
            DbValue.Text("order:" + request.OrderId.ToString("D")), bank ? DbValue.Null(NpgsqlTypes.NpgsqlDbType.Text) : DbValue.Text(request.Network == CryptoAsset.UsdtBsc ? "USDTBSC" : "USDTTRC20"),
            destination is null ? DbValue.Null(NpgsqlTypes.NpgsqlDbType.Bytea) : DbValue.Binary(destination), NullableText(key)), ct);
    }
    private sealed record SavedCard(byte[] Number, byte[] Holder, string Key);
}
