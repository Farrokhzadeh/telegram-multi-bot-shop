using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Entities;

namespace Platform.Persistence;

internal sealed partial class EfIntegrity(AkantiDbContext data, Func<bool> canWrite)
{
    private readonly HashSet<Guid> wallets = [], postings = [], audits = [], users = [];
    private readonly HashSet<Guid> orders = [], discounts = [], allocations = [], payments = [], entities = [];
    private bool rejected;
    internal bool AllowSetWrite { get; set; }
    internal void Reject() => rejected = true;
    internal void ThrowIfRejected()
    {
        if (rejected) throw new InvalidOperationException("The EF command was rejected and cannot commit.");
    }
    internal void RequireWrite()
    {
        ThrowIfRejected();
        if (canWrite()) return;
        Reject();
        throw new UnauthorizedAccessException("A verified writable EF command scope is required.");
    }
    internal void CaptureTracked()
    {
        RequireWrite();
        var entries = data.ChangeTracker.Entries().Where(value => value.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToArray();
        if (entries.Length > 256) throw new InvalidOperationException("The EF tracked mutation batch exceeds its bound.");
        foreach (var entry in entries)
        {
            if (entry.State != EntityState.Added && entry.Entity is WalletEntry or AuditEvent or DiscountRevision or SupportMessage or LegacyIdMap)
                throw new InvalidOperationException("Immutable EF history cannot change.");
            if (entry.State == EntityState.Deleted && entry.Entity is Wallet)
                throw new InvalidOperationException("A wallet cannot be deleted.");
            RequireImmutableOwner(entry);
            var previous = entry.State == EntityState.Added ? null : entry.OriginalValues.ToObject();
            if (previous is not null) RequireMutation(previous, entry.Entity);
            Capture(entry.Entity, entry.State == EntityState.Deleted);
            if (previous is not null) Capture(previous, entry.State == EntityState.Deleted);
        }
    }
    internal void Capture(object value, bool relatedAudit = false)
    {
        if (relatedAudit)
            foreach (var key in data.Entry(value).Metadata.FindPrimaryKey()!.Properties.Where(key => key.ClrType == typeof(Guid)))
                entities.Add((Guid)data.Entry(value).Property(key.Name).CurrentValue!);
        switch (value)
        {
            case Wallet wallet: wallets.Add(wallet.UserId); break;
            case WalletEntry entry: wallets.Add(entry.UserId); postings.Add(entry.Id); break;
            case AuditEvent audit: audits.Add(audit.Id); break;
            case User user: users.Add(user.Id); break;
            case Order order: orders.Add(order.Id); break;
            case Payment payment:
                payments.Add(payment.Id);
                if (payment.OrderId is { } paidOrder) orders.Add(paidOrder);
                break;
            case Fulfillment fulfillment: orders.Add(fulfillment.OrderId); break;
            case InventoryItem { AssignedFulfillmentId: { } assigned }: allocations.Add(assigned); break;
            case SupplierRequest request: orders.Add(request.OrderId); break;
            case Discount discount:
                discounts.Add(discount.Id);
                if (discount.SourceOrderId is { } source) orders.Add(source);
                break;
            case DiscountUse use: discounts.Add(use.DiscountId); orders.Add(use.OrderId); break;
            case DiscountRevision revision: discounts.Add(revision.DiscountId); break;
        }
        RequireScopeBound();
    }
    private void RequireScopeBound()
    {
        if (wallets.Count + postings.Count + audits.Count + users.Count + orders.Count + discounts.Count + allocations.Count + payments.Count + entities.Count > 1024)
            throw new InvalidOperationException("The EF command affected scope exceeds its bound.");
    }
    internal async Task ValidateAsync(CancellationToken ct)
    {
        ThrowIfRejected();
        ct.ThrowIfCancellationRequested();
        if (allocations.Count != 0)
            foreach (Guid id in await data.Set<Fulfillment>().Where(value => allocations.Contains(value.Id)).Select(value => value.OrderId).Distinct().ToArrayAsync(ct)) orders.Add(id);
        RequireScopeBound();
        await ValidateCommerceAsync(ct);
        if (wallets.Count != 0)
        {
            var ledger = data.Set<WalletEntry>();
            await RequireAsync(!await data.Set<Wallet>().AnyAsync(wallet => wallets.Contains(wallet.UserId) &&
                ((wallet.Version == 0 && wallet.Balance != 0) || (wallet.Version > 0 && !ledger.Any(entry => entry.UserId == wallet.UserId &&
                    entry.IsPosting && entry.PostingVersion == wallet.Version && entry.BalanceAfter == wallet.Balance))), ct), "wallet_posting_balance");
            await RequireAsync(!await ledger.AnyAsync(entry => postings.Contains(entry.Id) && entry.IsPosting &&
                (!data.Set<Wallet>().Any(wallet => wallet.UserId == entry.UserId && wallet.Version >= entry.PostingVersion) ||
                    (entry.PostingVersion == 1 && entry.BalanceAfter != entry.SignedAmount) ||
                    (entry.PostingVersion > 1 && !ledger.Any(previous => previous.UserId == entry.UserId && previous.IsPosting &&
                        previous.PostingVersion == entry.PostingVersion - 1 && previous.BalanceAfter + entry.SignedAmount == entry.BalanceAfter))), ct), "wallet_posting_chain");
        }
        if (postings.Count != 0 || orders.Count != 0 || payments.Count != 0)
        {
            var ledger = data.Set<WalletEntry>();
            await RequireAsync(!await ledger.AnyAsync(entry => (postings.Contains(entry.Id) || (entry.OrderId != null && orders.Contains(entry.OrderId.Value)) ||
                (entry.PaymentId != null && payments.Contains(entry.PaymentId.Value))) && entry.IsPosting &&
                ((entry.MovementKind == "late_crypto_credit" && !data.Set<Order>().Any(order => order.Id == entry.OrderId && order.UserId == entry.UserId &&
                    (order.PaymentStatus == "expired" || order.PaymentStatus == "cancelled") && data.Set<Payment>().Any(payment => payment.OrderId == order.Id &&
                        payment.Id == entry.PaymentId && payment.Status == "settled" && payment.CompensatingWalletEntryId == entry.Id &&
                        payment.PaymentMethod == "crypto" && payment.QuoteCurrency == "USD" && payment.AmountDue == entry.SignedAmount && payment.AmountReceived == payment.AmountDue))) ||
                    ((entry.MovementKind == "immediate_crypto_shortage_credit" || entry.MovementKind == "supplier_wallet_credit") &&
                        !data.Set<Order>().Any(order => order.Id == entry.OrderId && order.UserId == entry.UserId &&
                            order.ResolutionKind == entry.MovementKind && order.ResolutionWalletEntryId == entry.Id))), ct), "wallet_compensation_origin");
        }
        if (audits.Count != 0 || users.Count != 0)
        {
            await RequireAsync(!await data.Set<AuditEvent>().AnyAsync(audit => (audits.Contains(audit.Id) ||
                (audit.ActorUserId != null && users.Contains(audit.ActorUserId.Value))) && (audit.Reason.Trim() == "" ||
                    (audit.ActorKind == "customer" && !data.Set<User>().Any(user => user.Id == audit.ActorUserId && user.BrandId == audit.BrandId)) ||
                    (audit.EntityKind == "platform" && ((audit.ActorKind != "system" && audit.ActorKind != "admin") || audit.BrandId != null ||
                        (audit.Action != "maintenance" && audit.Action != "legacy_import")))), ct), "audit_customer_brand");
        }
        await ValidateAuditEntitiesAsync(ct);
    }
    private Task RequireAsync(bool valid, string code)
    {
        if (valid) return Task.CompletedTask;
        Reject();
        throw new InvalidOperationException("EF commit rejected: " + code + ".");
    }
}
