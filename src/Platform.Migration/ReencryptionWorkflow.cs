using System.Security.Cryptography;
using System.Text;
using Platform.Security;

namespace Platform.Migration;

public enum MigrationCipherField { FulfillmentContent, DiscountCode, BankDestination, SupportBody, BotToken }
public sealed class MigratedCiphertext
{
    private readonly byte[] ciphertext;
    internal MigratedCiphertext(byte[] ciphertext, string keyId) { this.ciphertext = ciphertext; KeyId = keyId; }
    public ReadOnlyMemory<byte> Ciphertext => ciphertext;
    public string KeyId { get; }
    public override string ToString() => "MigratedCiphertext [redacted]";
}
public sealed class ReencryptionWorkflow(SecretSnapshot target)
{
    public MigratedCiphertext Brand(BrandContext context, Guid row, MigrationCipherField field, string legacy, LegacyFieldKind sourceKind, IEnumerable<string> legacyKeys)
    {
        var location = field switch
        {
            MigrationCipherField.FulfillmentContent => ("fulfillments", "delivered_content"),
            MigrationCipherField.DiscountCode => ("discounts", "code_ciphertext"),
            MigrationCipherField.BankDestination => ("payments", "bank_destination_ciphertext"),
            MigrationCipherField.SupportBody => ("support_messages", "body_ciphertext"),
            MigrationCipherField.BotToken => ("brands", "bot_token_ciphertext"),
            _ => throw new MigrationRejectedException()
        };
        if (row == Guid.Empty || (field == MigrationCipherField.BotToken && row != context.BrandId)) throw new MigrationRejectedException();
        byte[] plain = Encoding.UTF8.GetBytes(LegacyFernetReader.Decrypt(legacy, sourceKind, legacyKeys));
        try
        {
            var cipher = new BrandCipher(target); string key = target.CurrentBrandKeyId(context);
            byte[] encrypted = cipher.Encrypt(context, location.Item1, row, location.Item2, plain);
            byte[] verified = cipher.Decrypt(context, location.Item1, row, location.Item2, key, encrypted);
            try { if (!CryptographicOperations.FixedTimeEquals(plain, verified)) throw new MigrationRejectedException(); }
            finally { CryptographicOperations.ZeroMemory(verified); }
            return new(encrypted, key);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public MigratedCiphertext Inventory(Guid row, string legacy, LegacyFieldKind sourceKind, IEnumerable<string> legacyKeys)
    {
        if (row == Guid.Empty) throw new MigrationRejectedException();
        byte[] plain = Encoding.UTF8.GetBytes(LegacyFernetReader.Decrypt(legacy, sourceKind, legacyKeys));
        try
        {
            var cipher = new InventoryCipher(target); string key = target.CurrentInventoryKeyId;
            byte[] encrypted = cipher.Encrypt(row, plain), verified = cipher.Decrypt(row, key, encrypted);
            try { if (!CryptographicOperations.FixedTimeEquals(plain, verified)) throw new MigrationRejectedException(); }
            finally { CryptographicOperations.ZeroMemory(verified); }
            return new(encrypted, key);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
