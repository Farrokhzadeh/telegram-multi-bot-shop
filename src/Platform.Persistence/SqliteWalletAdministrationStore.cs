using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
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
        byte[] commitment = audit.Fingerprint(actor.AdminId);
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            await SqliteAdminSessionStore.RequireActiveAsync(tx, actor, token);
            using (var prior = SqliteWallet.Command(tx, """
                SELECT command_sha256 FROM audit_events WHERE id=$id
                """, ("$id", value.CommandId)))
            {
                object? existing = await prior.ExecuteScalarAsync(token);
                if (existing is byte[] earlier)
                {
                    if (!CryptographicOperations.FixedTimeEquals(earlier, commitment))
                        throw new UnauthorizedAccessException("Administrator command identity conflict.");
                    return value.CommandId;
                }
            }
            using (var owner = SqliteWallet.Command(tx, """
                SELECT 1 FROM users u JOIN brands b ON b.id=u.brand_id
                WHERE u.id=$member AND b.id=$brand AND b.bot_id=$bot AND b.active=1
                """, ("$member", value.MemberId), ("$brand", context.BrandId),
                ("$bot", context.BotId)))
                if (await owner.ExecuteScalarAsync(token) is null)
                    throw new UnauthorizedAccessException("The wallet owner is unavailable.");
            using (var create = SqliteWallet.Command(tx, """
                INSERT INTO wallets(user_id) VALUES ($member) ON CONFLICT(user_id) DO NOTHING
                """, ("$member", value.MemberId)))
                await create.ExecuteNonQueryAsync(token);
            var wallet = await SqliteWallet.ReadAsync(tx, value.MemberId, token);
            if (wallet.Version != value.ExpectedVersion)
                throw new InvalidOperationException("Wallet version conflict.");
            Money balance = wallet.Balance.Add(amount);
            if (balance.Amount < 0) throw new InvalidOperationException("Wallet funds are insufficient.");
            string now = SqliteSchema.UtcText(DateTime.UtcNow);
            using (var eventWrite = SqliteWallet.Command(tx, """
                INSERT INTO audit_events(id,actor_kind,actor_admin_id,brand_id,action,entity_kind,entity_id,
                    reason,previous_revision,new_revision,correlation_id,command_sha256)
                VALUES ($id,'admin',$admin,$brand,'wallet_adjustment','wallet',$member,'operator_review',
                    $previous,$next,$id,$commitment)
                """, ("$id", value.CommandId), ("$admin", actor.AdminId),
                ("$brand", context.BrandId), ("$member", value.MemberId),
                ("$previous", value.ExpectedVersion), ("$next", checked(value.ExpectedVersion + 1)),
                ("$commitment", commitment)))
                if (await eventWrite.ExecuteNonQueryAsync(token) != 1)
                    throw new InvalidOperationException("Wallet adjustment audit was not persisted.");
            using (var update = SqliteWallet.Command(tx, """
                UPDATE wallets SET balance=$balance,version=version+1,updated_at=$now
                WHERE user_id=$member AND version=$version
                """, ("$balance", ExactDecimal.Format(balance.Amount)), ("$now", now),
                ("$member", value.MemberId), ("$version", wallet.Version)))
                if (await update.ExecuteNonQueryAsync(token) != 1)
                    throw new InvalidOperationException("Wallet adjustment version changed.");
            using var posting = SqliteWallet.Command(tx, """
                INSERT INTO wallet_entries(id,user_id,signed_amount,balance_after,posting_version,
                    movement_kind,is_posting,idempotency_key,admin_id,audit_event_id,reason)
                VALUES ($id,$member,$change,$balance,$version,'admin_adjustment',1,$key,$admin,$id,$reason)
                """, ("$id", value.CommandId), ("$member", value.MemberId),
                ("$change", ExactDecimal.Format(amount.Amount)),
                ("$balance", ExactDecimal.Format(balance.Amount)),
                ("$version", checked(wallet.Version + 1)),
                ("$key", "admin:" + value.CommandId.ToString("D")),
                ("$admin", actor.AdminId), ("$reason", value.Reason));
            if (await posting.ExecuteNonQueryAsync(token) != 1)
                throw new InvalidOperationException("Wallet adjustment posting was not persisted.");
            return value.CommandId;
        }, ct);
    }
}
