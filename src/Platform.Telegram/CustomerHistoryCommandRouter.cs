using System.Globalization;
using Platform.Persistence;
using Platform.Security;

namespace Platform.Telegram;

public sealed class CustomerHistoryCommandRouter(DbSessionFactory sessions, CustomerHistoryFiles files)
{
    public static bool TryParsePage(string value, out int offset)
    {
        offset = 0;
        if (value == "/orders") return true;
        if (!value.StartsWith("/orders ", StringComparison.Ordinal) || value.Length is < 9 or > 13 ||
            !int.TryParse(value.AsSpan(8), NumberStyles.None, CultureInfo.InvariantCulture, out int valueOffset) ||
            valueOffset is < 20 or > 10000 || valueOffset % 20 != 0 ||
            valueOffset.ToString(CultureInfo.InvariantCulture) != value[8..]) return false;
        offset = valueOffset;
        return true;
    }
    public async Task<CustomerHistoricalPage> ReadAsync(VerifiedTelegramUpdate update, CancellationToken ct = default)
    {
        if (update.IsCallback || !TryParsePage(update.Value, out int offset)) throw new TelegramIngressRejectedException();
        await using var session = await sessions.BeginReadOnlyAsync(ct);
        var member = await BrandUserStore.RequireAsync(session, update.Context, update.ActorId, null, ct);
        if (member.Blocked || member.Banned) throw new TelegramIngressRejectedException();
        var originals = await files.ReadOrdersAsync(update.Context, member.Id, ct);
        var imported = await CustomerHistoricalTypedAliasReader.ReadAsync(session, update.Context, member.Id, ct);
        await using var fresh = await sessions.BeginReadOnlyAsync(ct);
        var current = await BrandUserStore.RequireAsync(fresh, update.Context, update.ActorId, member.Id, ct);
        if (current.Blocked || current.Banned) throw new TelegramIngressRejectedException();
        return CustomerHistoricalOrderDeduplicator.Page(originals, imported, offset, requireArchiveMatch: true);
    }
}
