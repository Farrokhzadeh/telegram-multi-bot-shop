using Platform.Security;

namespace Platform.Persistence;

public sealed class SqliteCustomerScreens(SqliteSessionFactory sessions)
{
    public Task<CustomerHome> HomeAsync(BrandContext context, long actor, CancellationToken ct = default) =>
        sessions.RunEfAsync(false, null, async (data, token) => await CustomerScreenQueries.HomeAsync(data, await CustomerScreenQueries.MemberAsync(data, context, actor, token), token), ct);
    public Task SetLanguageAsync(BrandContext context, long actor, string language, CancellationToken ct = default)
    {
        BrandUserStore.RequireLanguage(language);
        return sessions.RunEfAsync(true, null, async (data, token) =>
        {
            var member = await data.RequireMembershipAsync(context, actor, false, false, token, true);
            data.Data.Attach(member!);
            member!.Language = language;
            return true;
        }, ct);
    }
    public Task<CustomerScreenPage> ListAsync(BrandContext context, long actor, CustomerScreenList screen, string filter, int page, CancellationToken ct = default) =>
        sessions.RunEfAsync(false, null, async (data, token) =>
            await CustomerScreenQueries.ListAsync(data, context, (await CustomerScreenQueries.MemberAsync(data, context, actor, token)).Id, screen, filter, page, token), ct);
    public Task<CustomerScreenEntry?> DetailAsync(BrandContext context, long actor, CustomerScreenList screen, Guid id, CancellationToken ct = default) =>
        sessions.RunEfAsync(false, null, async (data, token) =>
            await CustomerScreenQueries.DetailAsync(data, context, (await CustomerScreenQueries.MemberAsync(data, context, actor, token)).Id, screen, id, token), ct);
}
