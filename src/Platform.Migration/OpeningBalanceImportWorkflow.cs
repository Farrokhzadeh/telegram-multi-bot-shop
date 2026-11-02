using System.Security.Cryptography;
using System.Text;
using NpgsqlTypes;
using Platform.Core;
using Platform.Persistence;
using Platform.Security;

namespace Platform.Migration;

public sealed record LegacyHistoryRow(string LegacyId, Guid TargetEntry, decimal SignedAmount, string Currency, DateTime OccurredAt, decimal? BalanceAfter)
{
    public override string ToString() => "LegacyHistoryRow [redacted]";
}
public sealed record LegacyWalletSnapshot(string LegacyUser, Guid TargetUser, Guid TargetOpening, long TelegramId, string Language, DateTime FirstSeen, DateTime LastSeen,
    decimal BalanceUsd, string Currency, decimal OtherBalance, IReadOnlyList<LegacyHistoryRow> History)
{
    public IReadOnlyList<LegacyMapProof> Proofs(Guid brand)
    {
        Validate();
        byte[] member = Hash(w => { w.Write(LegacyUser); w.Write(TargetUser.ToByteArray()); w.Write(TelegramId); w.Write(Language); w.Write(FirstSeen.Ticks); w.Write(LastSeen.Ticks); });
        byte[] wallet = Hash(w => { w.Write(member); w.Write(TargetOpening.ToByteArray()); w.Write(ExactDecimal.Format(BalanceUsd)); w.Write(Currency); w.Write(ExactDecimal.Format(OtherBalance)); });
        var proofs = new List<LegacyMapProof> { new(LegacyProjection.User, LegacyUser, TargetUser, brand, member), new(LegacyProjection.WalletSnapshot, LegacyUser, TargetUser, brand, wallet) };
        if (BalanceUsd > 0) proofs.Add(new(LegacyProjection.WalletOpening, LegacyUser, TargetOpening, brand, wallet));
        foreach (var row in History)
        {
            byte[] hash = Hash(w => { w.Write(LegacyUser); w.Write(row.LegacyId); w.Write(row.TargetEntry.ToByteArray()); w.Write(ExactDecimal.Format(row.SignedAmount)); w.Write(row.Currency); w.Write(row.OccurredAt.Ticks); w.Write(row.BalanceAfter.HasValue); if (row.BalanceAfter is decimal balance) w.Write(ExactDecimal.Format(balance)); });
            proofs.Add(new(LegacyProjection.Transaction, row.LegacyId, row.TargetEntry, brand, hash));
        }
        return proofs.AsReadOnly();
    }
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(LegacyUser) || LegacyUser.Length > 256 || TargetUser == Guid.Empty || TargetOpening == Guid.Empty || TelegramId <= 0 || Language is not ("en" or "fa") ||
            FirstSeen.Kind != DateTimeKind.Utc || LastSeen.Kind != DateTimeKind.Utc || FirstSeen == DateTime.MinValue || LastSeen == DateTime.MaxValue || LastSeen < FirstSeen || FirstSeen.Ticks % 10 != 0 || LastSeen.Ticks % 10 != 0 || BalanceUsd < 0 || OtherBalance != 0 || Currency != "USD" || History.Count > 10000 ||
            History.Select(h => h.LegacyId).Distinct(StringComparer.Ordinal).Count() != History.Count || History.Select(h => h.TargetEntry).Distinct().Count() != History.Count ||
            History.Any(h => string.IsNullOrWhiteSpace(h.LegacyId) || h.TargetEntry == Guid.Empty || h.TargetEntry == TargetOpening || h.SignedAmount == 0 || h.Currency != "USD" || h.OccurredAt.Kind != DateTimeKind.Utc || h.OccurredAt == DateTime.MinValue || h.OccurredAt == DateTime.MaxValue || h.OccurredAt.Ticks % 10 != 0 || h.BalanceAfter < 0)) throw new MigrationRejectedException();
    }
    private static byte[] Hash(Action<BinaryWriter> write)
    { using var stream = new MemoryStream(); using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) { writer.Write("legacy-wallet-projection-v1"); write(writer); } return SHA256.HashData(stream.ToArray()); }
    public override string ToString() => "LegacyWalletSnapshot [redacted]";
}
public sealed class OpeningBalanceImportWorkflow(DbSessionFactory sessions)
{
    public async Task ImportAsync(BrandContext context, VerifiedImportManifest manifest, LegacyWalletSnapshot row, CancellationToken ct = default)
    {
        row = row with { History = Array.AsReadOnly(row.History.ToArray()) };
        row.Validate();
        if (manifest.TargetBrand != context.BrandId) throw new MigrationRejectedException();
        var proofs = row.Proofs(context.BrandId); foreach (var proof in proofs) manifest.Require(proof);
        await using var session = await sessions.BeginAsync(ct);
        await LegacyMapStore.RequireDestinationAsync(session, manifest, ct);
        bool brand = await session.QuerySingleAsync(new DbStatement("SELECT EXISTS(SELECT 1 FROM shop.brands WHERE id=$1 AND bot_id=$2)", DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId)), r => r.GetBoolean(0), ct);
        if (!brand) throw new MigrationRejectedException();
        bool newUser = await LegacyMapStore.EnsureAsync(session, manifest, proofs.Single(p => p.Projection == LegacyProjection.User), ct);
        if (newUser) await session.ExecuteAsync(new DbStatement("INSERT INTO shop.users(id,brand_id,telegram_id,language,first_seen_at,last_seen_at) VALUES ($1,$2,$3,$4,$5,$6)", DbValue.Uuid(row.TargetUser), DbValue.Uuid(context.BrandId), DbValue.Bigint(row.TelegramId), DbValue.Text(row.Language), DbValue.Utc(row.FirstSeen), DbValue.Utc(row.LastSeen)), ct);
        await session.QuerySingleAsync(new DbStatement("SELECT id FROM shop.users WHERE id=$1 AND brand_id=$2 AND telegram_id=$3 FOR UPDATE", DbValue.Uuid(row.TargetUser), DbValue.Uuid(context.BrandId), DbValue.Bigint(row.TelegramId)), r => r.GetGuid(0), ct);
        bool first = await LegacyMapStore.EnsureAsync(session, manifest, proofs.Single(p => p.Projection == LegacyProjection.WalletSnapshot), ct);
        if (first)
        {
            await session.ExecuteAsync(new DbStatement("INSERT INTO shop.wallets(user_id) VALUES ($1)", DbValue.Uuid(row.TargetUser)), ct);
            if (row.BalanceUsd > 0)
            {
                await LegacyMapStore.EnsureAsync(session, manifest, proofs.Single(p => p.Projection == LegacyProjection.WalletOpening), ct);
                await session.ExecuteAsync(new DbStatement("UPDATE shop.wallets SET balance=$1,version=1 WHERE user_id=$2", DbValue.Numeric(row.BalanceUsd), DbValue.Uuid(row.TargetUser)), ct);
                await session.ExecuteAsync(new DbStatement("INSERT INTO shop.wallet_entries(id,user_id,signed_amount,balance_after,posting_version,movement_kind,is_posting,idempotency_key,legacy_import_batch,reason) VALUES ($1,$2,$3,$3,1,'opening_balance',true,$4,$5,'Frozen authoritative USD opening; history is non-posting')",
                    DbValue.Uuid(row.TargetOpening), DbValue.Uuid(row.TargetUser), DbValue.Numeric(row.BalanceUsd), DbValue.Text("opening:" + row.TargetOpening.ToString("D")), DbValue.Uuid(manifest.Batch)), ct);
            }
            await session.ExecuteAsync(new DbStatement("INSERT INTO shop.audit_events(id,actor_kind,brand_id,action,entity_kind,entity_id,reason,correlation_id) VALUES ($1,'system',$2,'legacy_import','wallet',$3,'Frozen manifest-bound USD opening; no financial history replay',$4)", DbValue.Uuid(Guid.NewGuid()), DbValue.Uuid(context.BrandId), DbValue.Uuid(row.TargetUser), DbValue.Uuid(manifest.Batch)), ct);
        }
        foreach (var history in row.History)
        {
            var proof = proofs.Single(p => p.Projection == LegacyProjection.Transaction && p.LegacyId == history.LegacyId);
            if (!await LegacyMapStore.EnsureAsync(session, manifest, proof, ct)) continue;
            await session.ExecuteAsync(new DbStatement("INSERT INTO shop.wallet_entries(id,user_id,signed_amount,balance_after,movement_kind,is_posting,idempotency_key,legacy_import_batch,reason,created_at) VALUES ($1,$2,$3,$4,'legacy_history',false,$5,$6,'Non-posting source evidence; see protected frozen snapshot',$7)",
                DbValue.Uuid(history.TargetEntry), DbValue.Uuid(row.TargetUser), DbValue.Numeric(history.SignedAmount), history.BalanceAfter is decimal amount ? DbValue.Numeric(amount) : DbValue.Null(NpgsqlDbType.Numeric), DbValue.Text("history:" + history.TargetEntry.ToString("D")), DbValue.Uuid(manifest.Batch), DbValue.Utc(history.OccurredAt)), ct);
        }
        await session.CommitAsync(ct);

    }
}
