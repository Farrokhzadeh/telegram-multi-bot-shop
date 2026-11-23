using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Configurations;
using Platform.Security;

namespace Platform.Persistence;

internal static class CustomerScreenQueries
{
    internal sealed record Member(Guid Id, string Language, string Name, DateTime Joined, string? Contact);
    internal static async Task<Member> MemberAsync(EfSession data, BrandContext context, long actor, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(actor);
        var member = await (from value in data.Data.Set<Entities.User>()
                            join brand in data.Data.Set<Entities.Brand>() on value.BrandId equals brand.Id
                            where brand.Id == context.BrandId && brand.BotId == context.BotId && brand.Active && value.TelegramId == actor && value.BlockedAt == null && value.BannedAt == null
                            select new Member(value.Id, value.Language, brand.PublicName, value.FirstSeenAt,
                                data.Data.Set<Entities.Admin>().Where(admin => admin.Id == brand.CustomerSupportAdminId && admin.DisabledAt == null && admin.TelegramUserId != null &&
                                    admin.TelegramVerifiedAt != null && admin.TelegramVerificationAuditId != null).Select(admin => admin.PublicContactHandle).FirstOrDefault())).SingleOrDefaultAsync(ct);
        if (member is null) data.RejectScope("Customer membership is unavailable.");
        string? contact = member.Contact;
        if (contact is not null && (contact.Length is < 5 or > 32 || !contact.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))) contact = null;
        return member with { Contact = contact };
    }
    internal static async Task<CustomerHome> HomeAsync(EfSession data, Member member, CancellationToken ct)
    {
        decimal balance = await data.Data.Set<Entities.Wallet>().Where(value => value.UserId == member.Id).Select(value => (decimal?)value.Balance).SingleOrDefaultAsync(ct) ?? 0m;
        long orders = await data.Data.Set<Entities.Order>().LongCountAsync(value => value.UserId == member.Id, ct);
        decimal spent = await data.Data.Set<Entities.WalletEntry>().Where(value => value.UserId == member.Id && value.MovementKind == "purchase_debit")
            .Select(value => (decimal?)-value.SignedAmount).SumAsync(ct) ?? 0m;
        return new(member.Language, member.Name, balance, orders, spent, member.Joined, member.Contact);
    }
    internal static async Task<CustomerScreenPage> ListAsync(EfSession data, BrandContext context, Guid member, CustomerScreenList screen, string filter, int page, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(page);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(page, int.MaxValue / 5);
        if (filter is not ("all" or "active" or "finished")) throw new ArgumentOutOfRangeException(nameof(filter));
        var rows = await RowsAsync(data, context, member, screen, filter, checked(page * 5), 6, null, ct);
        return new(rows.Take(5).ToArray(), rows.Length > 5);
    }
    internal static async Task<CustomerScreenEntry?> DetailAsync(EfSession data, BrandContext context, Guid member, CustomerScreenList screen, Guid id, CancellationToken ct)
    {
        if (screen is not (CustomerScreenList.Orders or CustomerScreenList.Deposits)) throw new ArgumentOutOfRangeException(nameof(screen));
        return (await RowsAsync(data, context, member, screen, "all", 0, 1, id, ct)).SingleOrDefault();
    }
    private static Task<CustomerScreenEntry[]> RowsAsync(EfSession data, BrandContext context, Guid member, CustomerScreenList screen, string filter, int offset, int count, Guid? id, CancellationToken ct) => screen switch
    {
        CustomerScreenList.Orders => data.Data.Set<Entities.Order>().Where(value => value.UserId == member && (id == null || value.Id == id) && (filter == "all" ||
            ((value.FulfillmentStatus == "ready" || value.FulfillmentStatus == "completed" || value.FulfillmentStatus == "cancelled" || value.FulfillmentStatus == "compensated" || value.PaymentStatus == "expired" || value.PaymentStatus == "cancelled") == (filter == "finished"))))
            .OrderByDescending(value => value.CreatedAt).ThenByDescending(value => value.Id).Skip(offset).Take(count)
            .Select(value => new CustomerScreenEntry(value.Id, value.ProductTitleSnapshot, value.FulfillmentStatus,
                id != null && value.PaymentMethod == "wallet" ? data.Data.Set<Entities.WalletEntry>().Where(entry => entry.OrderId == value.Id && entry.UserId == member && entry.MovementKind == "purchase_debit").Select(entry => (decimal?)-entry.SignedAmount).FirstOrDefault() : null,
                id == null ? null : value.Currency, value.CreatedAt, value.Quantity)).ToArrayAsync(ct),
        CustomerScreenList.Transactions => data.Data.Set<Entities.WalletEntry>().Where(value => value.UserId == member && value.IsPosting)
            .OrderByDescending(value => value.CreatedAt).ThenByDescending(value => value.Id).Skip(offset).Take(count)
            .Select(value => new CustomerScreenEntry(value.Id, value.MovementKind, "", value.SignedAmount, "USD", value.CreatedAt, null)).ToArrayAsync(ct),
        CustomerScreenList.Deposits => data.Data.Set<Entities.Payment>().Where(value => (id == null || value.Id == id) && (value.TopupUserId == member || data.Data.Set<Entities.Order>().Any(order => order.Id == value.OrderId && order.UserId == member)))
            .OrderByDescending(value => value.CreatedAt).ThenByDescending(value => value.Id).Skip(offset).Take(count)
            .Select(value => new CustomerScreenEntry(value.Id, value.PaymentMethod, value.Status, value.AmountDue, value.QuoteCurrency, value.CreatedAt, null)).ToArrayAsync(ct),
        CustomerScreenList.Discounts => data.Data.Set<Entities.Discount>().Where(value => value.BrandId == context.BrandId && value.EligibleUserId == member && value.Active &&
            (value.StartsAt == null || value.StartsAt <= EfValues.UtcNow()) && (value.EndsAt == null || value.EndsAt > EfValues.UtcNow()))
            .OrderByDescending(value => value.CreatedAt).ThenByDescending(value => value.Id).Skip(offset).Take(count)
            .Select(value => new CustomerScreenEntry(value.Id, value.DisplayPrefix, value.Kind, value.PercentOff, "%", value.CreatedAt, null)).ToArrayAsync(ct),
        _ => throw new ArgumentOutOfRangeException(nameof(screen))
    };
}
