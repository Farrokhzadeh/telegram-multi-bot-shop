using Microsoft.EntityFrameworkCore;
using Platform.Core;
using Platform.Persistence.Entities;

namespace Platform.Persistence;

internal sealed partial class EfSession
{
    internal Task<int> LockWalletAsync(Guid member, CancellationToken ct) => LockRowsAsync(Data.Set<Wallet>().Where(value => value.UserId == member).Select(value => value.UserId), EfRowLock.Update, ct);
    internal async Task RequireWalletCommandAsync(Guid member, CancellationToken ct)
    {
        Guid brand = await Data.Set<User>().Where(value => value.Id == member).Select(value => value.BrandId).SingleAsync(ct);
        await LockUserAsync(member, brand, ct);
        if (nativeWriterBoundary) await RequireNativeBrandAsync(brand, ct);
        else if (writer is not null)
        {
            var registration = writer.Entries.SingleOrDefault(value => value.Context.BrandId == brand);
            if (registration is null) RejectScope("Wallet owner scope is unavailable.");
            await RequireWriterAsync(registration.Context, ct);
        }
        else if (!Data.Database.IsSqlite()) RejectScope("Wallet command scope is unavailable.");
        admitted = true;
    }
}
internal static class WalletOperations
{
    internal static async Task<(Money Balance, long Version)> ReadAsync(EfSession data, Guid member, CancellationToken ct, bool locked = false)
    {
        if (locked) await data.LockWalletAsync(member, ct);
        var row = await data.Data.Set<Wallet>().AsNoTracking().Where(value => value.UserId == member).Select(value => new
        {
            value.Balance,
            value.Version,
            Posted = data.Data.Set<WalletEntry>().Where(entry => entry.UserId == member && entry.IsPosting && entry.PostingVersion == value.Version).Select(entry => entry.BalanceAfter).SingleOrDefault()
        }).SingleOrDefaultAsync(ct);
        if (row is null) return (new Money(0, Currency.Usd), 0);
        if (row.Version == 0 ? row.Balance != 0 : row.Posted != row.Balance) data.RejectConflict("Wallet balance is missing its matching posting.");
        return (new Money(row.Balance, Currency.Usd), row.Version);
    }
    internal static async Task<Guid> PostAsync(EfSession data, Guid member, Money change, long? expected, WalletEntry entry, CancellationToken ct, DateTime? now = null)
    {
        await data.RequireWalletCommandAsync(member, ct);
        var wallet = await ReadAsync(data, member, ct, true);
        if (expected is long version && wallet.Version != version) data.RejectConflict("Wallet version conflict.");
        Money balance = wallet.Balance.Add(change);
        if (balance.Amount < 0) data.RejectConflict("Wallet funds are insufficient.");
        var row = await data.Data.Set<Wallet>().SingleOrDefaultAsync(value => value.UserId == member, ct);
        if (row is null)
        {
            row = new Wallet { UserId = member };
            data.Data.Add(row);
            await data.FlushAsync(ct, false);
        }
        row.Balance = balance.Amount; row.Version = checked(wallet.Version + 1);
        row.UpdatedAt = now ?? await data.ClockAsync(data.Data.Set<User>().Where(value => value.Id == member), ct);
        entry.UserId = member; entry.SignedAmount = change.Amount; entry.BalanceAfter = balance.Amount; entry.PostingVersion = row.Version; entry.IsPosting = true;
        data.Data.Add(entry);
        return entry.Id;
    }
    internal static Task<(Money Balance, long Version)> ReadAsync(DbSession session, Guid member, CancellationToken ct) => session.RunEfStepAsync(false, (data, token) => ReadAsync(data, member, token, true), ct);
    internal static Task<Guid> PostAsync(DbSession session, Guid member, Money change, long? expected, WalletEntry entry, CancellationToken ct) => session.RunEfStepAsync(true, (data, token) => PostAsync(data, member, change, expected, entry, token), ct);
}
