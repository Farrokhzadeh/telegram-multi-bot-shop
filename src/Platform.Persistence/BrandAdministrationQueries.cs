using Microsoft.EntityFrameworkCore;
using Platform.Security;

namespace Platform.Persistence;

internal static class BrandAdministrationQueries
{
    internal static async Task<AdminPage<AdminBrand>> ListAsync(EfSession data, AuthenticatedAdmin actor, Guid? after, int limit, CancellationToken ct)
    {
        AdminCommand.Page(limit);
        await AdminSessionStore.RequireActiveAsync(data, actor, null, ct);
        var rows = await data.Data.Set<Entities.Brand>().Where(value => after == null || value.Id.CompareTo(after.Value) > 0).OrderBy(value => value.Id).Take(limit + 1)
            .Select(value => new AdminBrand(value.Id, value.PublicName, value.Market, value.DefaultLanguage, value.Active, value.PolicyVersion)).ToArrayAsync(ct);
        return new(rows.Take(limit).ToArray(), rows.Length > limit ? rows[limit - 1].Id : null);
    }
    internal static async Task<BrandPolicyDetail> ReadAsync(EfSession data, AuthenticatedAdmin actor, BrandContext context, CancellationToken ct)
    {
        await AdminSessionStore.RequireActiveAsync(data, actor, null, ct);
        if (data.Data is PostgresAkantiDbContext) await data.LockBrandAsync(context.BrandId, ct, true);
        var brand = await data.Data.Set<Entities.Brand>().AsNoTracking().SingleOrDefaultAsync(value => value.Id == context.BrandId && value.BotId == context.BotId, ct);
        if (brand is null) data.RejectScope("The brand policy scope is unavailable.");
        var channels = await data.Data.Set<Entities.BrandChannel>().Where(value => value.BrandId == context.BrandId && value.Active && value.Required).OrderBy(value => value.ChatId)
            .Select(value => new RequiredChannel(value.ChatId, value.Title, value.InviteLink)).Take(1001).ToArrayAsync(ct);
        if (channels.Length > 1000) throw new InvalidOperationException("Channel result limit exceeded.");
        return new(new BrandPolicyEdit(brand.PublicName, brand.DefaultLanguage, brand.Active, brand.AllowWallet, brand.AllowCrypto, brand.AllowBank, brand.AllowWalletTopup,
            brand.ZeroBalanceTopup, brand.SupportEnabled, brand.CustomerSupportAdminId, brand.FxEnabled, brand.LogoAssetRef), brand.RequireChannelJoin, channels, brand.PolicyVersion);
    }
    internal static async Task<long> WriteAsync(EfSession data, AuthenticatedAdmin actor, BrandContext context, BrandPolicyEdit value, long revision, CancellationToken ct)
    {
        await AdminSessionStore.RequireActiveAsync(data, actor, null, ct);
        if (value.Support && !await data.Data.Set<Entities.Admin>().AnyAsync(admin => admin.Id == value.ContactAdminId && admin.DisabledAt == null && admin.TelegramVerifiedAt != null && admin.PublicContactHandle != null, ct))
            data.RejectArgument("A current verified public support contact is required.");
        await data.LockBrandAsync(context.BrandId, ct);
        var brand = await data.Data.Set<Entities.Brand>().SingleOrDefaultAsync(brand => brand.Id == context.BrandId && brand.BotId == context.BotId && brand.PolicyVersion == revision, ct);
        if (brand is null) data.RejectConflict("Policy revision conflict.");
        brand.PublicName = value.PublicName; brand.DefaultLanguage = value.Language; brand.Active = value.Active;
        brand.AllowWallet = value.Wallet; brand.AllowCrypto = value.Crypto; brand.AllowBank = value.Bank; brand.AllowWalletTopup = value.Topup;
        brand.ZeroBalanceTopup = value.ZeroBalanceTopup; brand.SupportEnabled = value.Support; brand.CustomerSupportAdminId = value.ContactAdminId;
        brand.FxEnabled = value.Fx; brand.LogoAssetRef = value.Logo; brand.PolicyVersion++;
        brand.UpdatedAt = await data.BrandClockAsync(context.BrandId, ct);
        return checked(revision + 1);
    }
}
