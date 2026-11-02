using Platform.Security;

namespace Platform.Persistence;

public static class TelegramReplayStore
{
    public static async Task<bool> TryClaimAsync(DbSession session, BrandContext context, long updateId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (updateId < 0) throw new ArgumentOutOfRangeException(nameof(updateId));
        var prior = await session.QuerySingleAsync(new DbStatement("SELECT telegram_update_highwater,telegram_update_window FROM shop.brands WHERE id=$1 AND bot_id=$2 AND active FOR UPDATE",
            DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId)), r => (High: r.GetInt64(0), Bits: r.GetBinary(1)), ct);
        long high = Math.Max(prior.High, updateId);
        if (high - updateId >= 1024) return false;
        int offset = (int)(high - updateId);
        if (high == prior.High && (prior.Bits[offset / 8] & (1 << (offset % 8))) != 0) return false;
        byte[] bits = new byte[128];
        long shift = prior.High < 0 ? 1024 : high - prior.High;
        if (shift < 1024)
            for (int i = 0; i < 1024 - (int)shift; i++)
                if ((prior.Bits[i / 8] & (1 << (i % 8))) != 0) { int next = i + (int)shift; bits[next / 8] |= (byte)(1 << (next % 8)); }
        bits[offset / 8] |= (byte)(1 << (offset % 8));
        await session.ExecuteAsync(new DbStatement("UPDATE shop.brands SET telegram_update_highwater=$1,telegram_update_window=$2 WHERE id=$3 AND bot_id=$4",
            DbValue.Bigint(high), DbValue.Binary(bits), DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId)), ct);
        return true;
    }
}
