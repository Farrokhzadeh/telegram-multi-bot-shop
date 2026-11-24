using Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace Platform.Persistence;

public static class TelegramReplayStore
{
    public static async Task<bool> TryClaimAsync(DbSession session, BrandContext context, long updateId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (updateId < 0) throw new ArgumentOutOfRangeException(nameof(updateId));
        return await session.RunEfStepAsync(true, async (data, token) =>
        {
            await data.RequireBrandCommandAsync(context.BrandId, token); await data.LockBrandUpdateAsync(context.BrandId, token);
            var row = await data.Data.Set<Entities.Brand>().SingleAsync(row => row.Id == context.BrandId && row.BotId == context.BotId && row.Active, token);
            var prior = (High: row.TelegramUpdateHighwater, Bits: row.TelegramUpdateWindow);
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
            row.TelegramUpdateHighwater = high; row.TelegramUpdateWindow = bits; return true;
        }, ct);
    }
}
