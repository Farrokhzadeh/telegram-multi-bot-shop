using Platform.Security;

namespace Platform.Persistence;

public sealed record CustomerHome(string Language, string Brand, decimal Balance, long Orders, decimal Spent, DateTime Joined, string? Contact);
public sealed record CustomerScreenEntry(Guid Id, string Title, string Status, decimal? Amount, string? Currency, DateTime Created, int? Quantity = null);
public sealed record CustomerScreenPage(IReadOnlyList<CustomerScreenEntry> Entries, bool HasNext);
public enum CustomerScreenList { Orders, Transactions, Deposits, Discounts }

public sealed class CustomerScreens(DbSessionFactory sessions)
{
    public async Task<CustomerHome> HomeAsync(BrandContext context, long actor, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginAsync(ct);
        BrandPolicySnapshot policy = await BrandPolicyStore.ReadAsync(session, context, actor, ct);
        return await session.RunEfStepAsync(false, async (data, token) =>
        {
            var member = await CustomerScreenQueries.MemberAsync(data, context, actor, token);
            return await CustomerScreenQueries.HomeAsync(data, member with { Language = policy.Language, Name = policy.PublicName, Contact = policy.PublicContact }, token);
        }, ct);
    }

    public async Task SetLanguageAsync(BrandContext context, long actor, string language, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginAsync(ct);
        BrandUser member = await MemberAsync(session, context, actor, ct);
        await BrandUserStore.SetPreferencesAsync(session, context, actor, member.Id, language, member.MarketingOptOut, ct);
        await session.CommitAsync(ct);
    }

    public async Task<CustomerScreenPage> ListAsync(BrandContext context, long actor, CustomerScreenList screen,
        string filter, int page, CancellationToken ct = default)
    {
        const int pageSize = 5;
        ArgumentOutOfRangeException.ThrowIfNegative(page);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(page, int.MaxValue / pageSize);
        if (filter is not ("all" or "active" or "finished")) throw new ArgumentOutOfRangeException(nameof(filter));
        await using var session = await sessions.BeginReadOnlyAsync(ct);
        BrandUser member = await MemberAsync(session, context, actor, ct);
        return await session.RunEfStepAsync(false, (data, token) => CustomerScreenQueries.ListAsync(data, context, member.Id, screen, filter, page, token), ct);
    }

    public async Task<CustomerScreenEntry?> DetailAsync(BrandContext context, long actor, CustomerScreenList list,
        Guid id, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginReadOnlyAsync(ct);
        BrandUser member = await MemberAsync(session, context, actor, ct);
        return await session.RunEfStepAsync(false, (data, token) => CustomerScreenQueries.DetailAsync(data, context, member.Id, list, id, token), ct);
    }

    private static async Task<BrandUser> MemberAsync(DbSession session, BrandContext context, long actor, CancellationToken ct)
    {
        var member = await BrandUserStore.RequireAsync(session, context, actor, null, ct);
        if (member.Blocked || member.Banned) throw new UnauthorizedAccessException();
        return member;
    }
}
