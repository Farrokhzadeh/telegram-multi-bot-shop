using Platform.Core;
using Platform.Security;
using Npgsql;
using Microsoft.EntityFrameworkCore;

namespace Platform.Persistence;

public sealed class AiContextStore
{
    private readonly DbSessionFactory reads;
    public AiContextStore(NpgsqlDataSource data) => reads = new(data, new DbSessionOptions { ReadOnly = true });
    public async Task<AiShopContext?> ReadAsync(AiReadScope scope, CancellationToken ct = default)
    {
        if (!scope.IsActive) throw new UnauthorizedAccessException();
        await using var session = await reads.BeginAsync(ct);
        var members = await session.RunEfStepAsync(false, (data, token) => (from u in data.Data.Set<Entities.User>() join b in data.Data.Set<Entities.Brand>() on u.BrandId equals b.Id join a in data.Data.Set<Entities.Admin>() on b.CustomerSupportAdminId equals a.Id into admins from a in admins.DefaultIfEmpty() where u.BrandId == scope.Brand.BrandId && b.BotId == scope.Brand.BotId && u.TelegramId == scope.ActorId && b.Active && u.BlockedAt == null && u.BannedAt == null select new { Member = u.Id, Brand = b.PublicName, u.Language, DefaultLanguage = b.DefaultLanguage, Support = b.SupportEnabled && a != null && a.DisabledAt == null && a.TelegramUserId != null && a.PublicContactHandle != null, Contact = b.SupportEnabled && a != null && a.DisabledAt == null && a.TelegramUserId != null ? a.PublicContactHandle : null }).Take(1).ToListAsync(token), ct);
        if (members.Count != 1) return null;
        var member = members[0]; AiOrderContext? order = null;
        if (scope.OrderId is Guid id)
        {
            var orders = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.Order>().Where(row => row.Id == id && row.UserId == member.Member).Select(row => new AiOrderContext(row.Id, row.ProductTitleSnapshot, row.PaymentStatus, row.FulfillmentStatus)).Take(1).ToListAsync(token), ct);
            if (orders.Count != 1) return null; order = orders[0];
        }
        if (!scope.IsActive) throw new UnauthorizedAccessException();
        string language = member.Language.ToLowerInvariant().Split('-')[0];
        if (language is not ("en" or "fa")) language = member.DefaultLanguage is "en" or "fa" ? member.DefaultLanguage : "en";
        return new(1, scope.Brand.BrandId, member.Brand, language, member.Support, member.Contact, order);
    }
}
