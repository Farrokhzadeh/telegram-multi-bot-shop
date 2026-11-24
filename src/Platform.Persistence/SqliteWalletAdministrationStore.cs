using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

public sealed class SqliteWalletAdministrationStore(SqliteSessionFactory sessions)
{
    public Task<Guid> AdjustAsync(AuthenticatedAdmin actor, BrandContext context, WalletAdjustment value,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(context);
        AdminCommand.Reason(value.Reason);
        AdminCommand.Revision(value.ExpectedVersion);
        Money amount = new(ExactDecimal.Parse(value.AmountUsd), Currency.Usd);
        if (amount.Amount == 0 || value.MemberId == Guid.Empty || value.CommandId == Guid.Empty)
            throw new ArgumentException("A nonzero authorized wallet adjustment is required.");
        byte[] hash = AdminCommand.Hash("wallet-adjustment-v1", ExactDecimal.Format(amount.Amount),
            value.ExpectedVersion.ToString(CultureInfo.InvariantCulture), value.Reason);
        var audit = new AuditCommand(value.CommandId, AuditAction.WalletAdjustment, AuditEntity.Wallet,
            value.MemberId, context.BrandId, AuditReason.OperatorReview, value.CommandId, hash,
            value.ExpectedVersion, checked(value.ExpectedVersion + 1));
        return sessions.RunEfAsync(true, null, async (data, token) =>
        {
            if (!await AdminAuditStore.AppendEfAsync(data, actor, audit, token)) return value.CommandId;
            if (!await (from member in data.Data.Set<Entities.User>()
                        join brand in data.Data.Set<Entities.Brand>() on member.BrandId equals brand.Id
                        where member.Id == value.MemberId && brand.Id == context.BrandId && brand.BotId == context.BotId && brand.Active
                        select member.Id).AnyAsync(token)) data.RejectScope("The wallet owner is unavailable.");
            return await WalletOperations.PostAsync(data, value.MemberId, amount, value.ExpectedVersion,
                new Entities.WalletEntry { Id = value.CommandId, MovementKind = "admin_adjustment", IdempotencyKey = "admin:" + value.CommandId.ToString("D"), AdminId = actor.AdminId, AuditEventId = value.CommandId, Reason = value.Reason }, token);
        }, ct);
    }
}
