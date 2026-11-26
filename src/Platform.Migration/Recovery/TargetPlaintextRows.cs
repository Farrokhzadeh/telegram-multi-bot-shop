using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Platform.Persistence;
using Platform.Persistence.Entities;
using Platform.Security;

namespace Platform.Migration.Recovery;

internal sealed class TargetPlaintextRows(SecretSnapshot keys, Func<Guid, BrandContext> contextFor)
{
    internal async Task CopyAsync<T>(AkantiDbContext source, PlainTargetCopyDbContext target, T original, T destination, CancellationToken ct, bool track = true) where T : class
    {
        if (source is PlainTargetDbContext || source.Entry(original).Metadata.ClrType != target.Entry(destination).Metadata.ClrType ||
            ReferenceEquals(original, destination) || target.Entry(destination).State != EntityState.Detached)
            throw new MigrationRejectedException();

        var old = source.Entry(original);
        var next = target.Entry(destination);
        foreach (var property in next.Properties)
        {
            string name = property.Metadata.Name;
            if (old.Metadata.FindProperty(name) is null)
            {
                if ((original, name) is (Brand, nameof(Brand.BotToken) or nameof(Brand.BankNumber) or nameof(Brand.BankHolder)) or
                    (InventoryItem, nameof(InventoryItem.CredentialContent)) or
                    (Payment, nameof(Payment.BankDestination) or nameof(Payment.PendingEvidence)) or
                    (SupportMessage, nameof(SupportMessage.BodyContent)) or (Discount, nameof(Discount.CodeContent))) continue;
                throw new MigrationRejectedException();
            }
            object? value = old.Property(name).CurrentValue;
            property.CurrentValue = value is byte[] bytes ? bytes.ToArray() : value;
        }

        var plaintext = new List<byte[]>(3);
        try
        {
            switch (original, destination)
            {
                case (Brand prior, Brand to):
                    var brand = Resolve(prior.Id);
                    bool hasBank = prior.BankNumberCiphertext is not null;
                    if (brand.BotId != prior.BotId || hasBank != (prior.BankHolderCiphertext is not null) ||
                        hasBank != (prior.BankCardFingerprint is not null) || hasBank != prior.BankChangedAt.HasValue)
                        throw new MigrationRejectedException();
                    to.BotToken = Open(brand, "brands", prior.Id, "bot_token_ciphertext", prior.BotTokenKeyId, prior.BotTokenCiphertext, true)!;
                    to.BankNumber = Open(brand, "brands", prior.Id, "bank_number_ciphertext", prior.BankKeyId, prior.BankNumberCiphertext);
                    to.BankHolder = Open(brand, "brands", prior.Id, "bank_holder_ciphertext", prior.BankKeyId, prior.BankHolderCiphertext);
                    break;
                case (InventoryItem prior, InventoryItem to):
                    if (prior.CredentialCiphertext is null || string.IsNullOrEmpty(prior.CredentialKeyId)) throw new MigrationRejectedException();
                    byte[] credential = new InventoryCipher(keys).Decrypt(prior.Id, prior.CredentialKeyId, prior.CredentialCiphertext);
                    plaintext.Add(credential);
                    if (credential.Length == 0) throw new MigrationRejectedException();
                    to.CredentialContent = credential;
                    break;
                case (Payment prior, Payment to):
                    if (prior.BankDestinationCiphertext is null && prior.PendingEvidenceCiphertext is null)
                    {
                        if (prior.BankDestinationKeyId is not null || prior.PendingEvidenceKeyId is not null) throw new MigrationRejectedException();
                        break;
                    }
                    var member = await (from user in source.Set<User>()
                        where user.Id == prior.TopupUserId || source.Set<Order>().Any(order => order.Id == prior.OrderId && order.UserId == user.Id)
                        select (Guid?)user.BrandId).SingleOrDefaultAsync(ct) ?? throw new MigrationRejectedException();
                    var paymentBrand = Resolve(member);
                    to.BankDestination = Open(paymentBrand, "payments", prior.Id, "bank_destination_ciphertext", prior.BankDestinationKeyId, prior.BankDestinationCiphertext);
                    to.PendingEvidence = Open(paymentBrand, "payments", prior.Id, "ipn_evidence", prior.PendingEvidenceKeyId, prior.PendingEvidenceCiphertext);
                    if (to.PendingEvidence is { } evidence &&
                        (evidence.Length is < 2 or > 65536 || prior.EvidenceDigest is null ||
                            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(evidence), prior.EvidenceDigest)))
                        throw new MigrationRejectedException();
                    break;
                case (Fulfillment prior, Fulfillment to):
                    if (prior.DeliveredContent is null && prior.DeliveredContentKeyId is null) break;
                    var fulfillmentBrand = await (from order in source.Set<Order>() join user in source.Set<User>() on order.UserId equals user.Id
                        where order.Id == prior.OrderId select (Guid?)user.BrandId).SingleOrDefaultAsync(ct) ?? throw new MigrationRejectedException();
                    to.DeliveredContent = Open(Resolve(fulfillmentBrand), "fulfillments", prior.Id, "delivered_content", prior.DeliveredContentKeyId, prior.DeliveredContent);
                    break;
                case (SupportMessage prior, SupportMessage to):
                    if (prior.BodyCiphertext is null && prior.BodyKeyId is null) break;
                    var supportBrand = await (from ticket in source.Set<SupportTicket>() join user in source.Set<User>() on ticket.UserId equals user.Id
                        where ticket.Id == prior.TicketId select (Guid?)user.BrandId).SingleOrDefaultAsync(ct) ?? throw new MigrationRejectedException();
                    to.BodyContent = Open(Resolve(supportBrand), "support_messages", prior.Id, "body_ciphertext", prior.BodyKeyId, prior.BodyCiphertext);
                    break;
                case (Discount prior, Discount to):
                    to.CodeContent = Open(Resolve(prior.BrandId), "discounts", prior.Id, "code_ciphertext", prior.CodeKeyId, prior.CodeCiphertext, true)!;
                    break;
            }
            ct.ThrowIfCancellationRequested();
            if (track) target.Add(destination);
        }
        catch (CipherRejectedException)
        {
            Clear();
            throw new MigrationRejectedException();
        }
        catch
        {
            Clear();
            throw;
        }

        byte[]? Open(BrandContext brand, string table, Guid id, string field, string? key, byte[]? cipher, bool required = false)
        {
            if (cipher is null && key is null && !required) return null;
            if (cipher is null || string.IsNullOrEmpty(key)) throw new MigrationRejectedException();
            byte[] value = new BrandCipher(keys).Decrypt(brand, table, id, field, key, cipher);
            plaintext.Add(value);
            if (value.Length == 0) throw new MigrationRejectedException();
            return value;
        }
        void Clear()
        {
            foreach (byte[] value in plaintext) CryptographicOperations.ZeroMemory(value);
            next.State = EntityState.Detached;
        }
    }

    private BrandContext Resolve(Guid id)
    {
        var context = contextFor(id);
        if (context.BrandId != id) throw new MigrationRejectedException();
        return context;
    }
}
