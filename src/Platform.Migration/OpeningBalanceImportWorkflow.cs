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
public sealed record LegacyMemberState(string? Username, string? FirstName, string? LastName, bool Active, bool Banned, DateTime ObservedAt, DateTime? BannedAt = null)
{
    public override string ToString() => "LegacyMemberState [redacted]";
}
public sealed record LegacyMembershipClock(DateTime JoinedAt, DateTime LastSeenAt)
{
    public override string ToString() => "LegacyMembershipClock [redacted]";
}
public sealed record LegacyWalletSnapshot(string LegacyUser, Guid TargetUser, Guid TargetOpening, long TelegramId, string Language, DateTime FirstSeen, DateTime LastSeen,
    decimal BalanceUsd, string Currency, decimal OtherBalance, IReadOnlyList<LegacyHistoryRow> History, LegacyMemberState? State = null, LegacyWalletRetention? Retention = null, LegacyMembershipClock? SourceClock = null)
{
    public IReadOnlyList<LegacyMapProof> Proofs(Guid brand)
    {
        Validate();
        byte[] member = Hash(w => { w.Write(LegacyUser); w.Write(TargetUser.ToByteArray()); w.Write(TelegramId); w.Write(Language); w.Write(FirstSeen.Ticks); w.Write(LastSeen.Ticks); if (SourceClock is not null) { w.Write("legacy-membership-clock-v1"); w.Write(SourceClock.JoinedAt.Ticks); w.Write(SourceClock.LastSeenAt.Ticks); } if (State is not null) { w.Write("legacy-member-state-v1"); foreach (string? value in new[] { State.Username, State.FirstName, State.LastName }) { w.Write(value is not null); if (value is not null) w.Write(value); } w.Write(State.Active); w.Write(State.Banned); w.Write(State.ObservedAt.Ticks); if (State.BannedAt is DateTime banned) { w.Write("legacy-ban-time-v1"); w.Write(banned.Ticks); } } });
        byte[] wallet = Hash(w => { w.Write(member); w.Write(TargetOpening.ToByteArray()); w.Write(ExactDecimal.Format(BalanceUsd)); w.Write(Currency); w.Write(ExactDecimal.Format(OtherBalance)); if (Retention is not null) { w.Write("legacy-wallet-retention-v2"); w.Write(Retention.Reference); w.Write(Retention.Digest.Span); } });
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
        Retention?.RequireRow(LegacyUser, OtherBalance, History);
        if (SourceClock is not null && (SourceClock.JoinedAt.Kind != DateTimeKind.Utc || SourceClock.LastSeenAt.Kind != DateTimeKind.Utc || SourceClock.JoinedAt.Ticks % 10 != 0 || SourceClock.LastSeenAt.Ticks % 10 != 0 || SourceClock.JoinedAt <= SourceClock.LastSeenAt || FirstSeen != SourceClock.LastSeenAt || LastSeen != SourceClock.JoinedAt)) throw new MigrationRejectedException();
        if (State is not null && (State.ObservedAt.Kind != DateTimeKind.Utc || State.ObservedAt < LastSeen || State.ObservedAt == DateTime.MaxValue || State.ObservedAt.Ticks % 10 != 0 || (State.BannedAt is DateTime banned && (!State.Banned || banned.Kind != DateTimeKind.Utc || banned < FirstSeen || banned > State.ObservedAt || banned.Ticks % 10 != 0)) || new[] { State.Username, State.FirstName, State.LastName }.Any(v => v is not null && (string.IsNullOrWhiteSpace(v) || v.Length > 160)))) throw new MigrationRejectedException();
        if (string.IsNullOrWhiteSpace(LegacyUser) || LegacyUser.Length > 256 || TargetUser == Guid.Empty || TargetOpening == Guid.Empty || TelegramId <= 0 || Language is not ("en" or "fa") ||
            FirstSeen.Kind != DateTimeKind.Utc || LastSeen.Kind != DateTimeKind.Utc || FirstSeen == DateTime.MinValue || LastSeen == DateTime.MaxValue || LastSeen < FirstSeen || FirstSeen.Ticks % 10 != 0 || LastSeen.Ticks % 10 != 0 || BalanceUsd < 0 || (OtherBalance != 0 && Retention is null) || Currency != "USD" || History.Count > 10000 ||
            History.Select(h => h.LegacyId).Distinct(StringComparer.Ordinal).Count() != History.Count || History.Select(h => h.TargetEntry).Distinct().Count() != History.Count ||
            History.Any(h => string.IsNullOrWhiteSpace(h.LegacyId) || h.TargetEntry == Guid.Empty || h.TargetEntry == TargetOpening || h.SignedAmount == 0 || h.Currency != "USD" || h.OccurredAt.Kind != DateTimeKind.Utc || h.OccurredAt == DateTime.MinValue || h.OccurredAt == DateTime.MaxValue || h.OccurredAt.Ticks % 10 != 0 || h.BalanceAfter < 0)) throw new MigrationRejectedException();
    }
    private static byte[] Hash(Action<BinaryWriter> write)
    { using var stream = new MemoryStream(); using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) { writer.Write("legacy-wallet-projection-v1"); write(writer); } return SHA256.HashData(stream.ToArray()); }
    public override string ToString() => "LegacyWalletSnapshot [redacted]";
}
public sealed class OpeningBalanceImportWorkflow(DbSessionFactory sessions)
{
    public Task ImportAsync(BrandContext context, VerifiedImportManifest manifest, LegacyWalletSnapshot row, CancellationToken ct = default, Func<string, CancellationToken, Task<ReadOnlyMemory<byte>>>? readRetention = null) => ImportCoreAsync(context, manifest, row, readRetention, null, ct);
    internal Task ImportVerifiedAsync(BrandContext context, VerifiedImportManifest manifest, LegacyWalletSnapshot row, WalletRetentionVerification retention, CancellationToken ct) => ImportCoreAsync(context, manifest, row, null, retention, ct);
    private async Task ImportCoreAsync(BrandContext context, VerifiedImportManifest manifest, LegacyWalletSnapshot row, Func<string, CancellationToken, Task<ReadOnlyMemory<byte>>>? readRetention, WalletRetentionVerification? retention, CancellationToken ct)
    {
        row = row with { History = Array.AsReadOnly(row.History.ToArray()) };
        row.Validate();
        row.Retention?.Require(manifest);
        if (row.Retention is not null && !(retention is null ? await row.Retention.MatchesAsync(readRetention, ct) : retention.Matches(row.Retention))) throw new MigrationRejectedException();
        if (manifest.TargetBrand != context.BrandId) throw new MigrationRejectedException();
        var proofs = row.Proofs(context.BrandId); foreach (var proof in proofs) manifest.Require(proof);
        await using var session = await sessions.BeginAsync(ct);
        await LegacyMapStore.RequireDestinationAsync(session, manifest, ct);
        bool brand = await session.QuerySingleAsync(new DbStatement("SELECT EXISTS(SELECT 1 FROM shop.brands WHERE id=$1 AND bot_id=$2)", DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId)), r => r.GetBoolean(0), ct);
        if (!brand) throw new MigrationRejectedException();
        bool newUser = await LegacyMapStore.EnsureAsync(session, manifest, proofs.Single(p => p.Projection == LegacyProjection.User), ct);
        if (newUser) await session.ExecuteAsync(new DbStatement("INSERT INTO shop.users(id,brand_id,telegram_id,language,first_seen_at,last_seen_at,username,first_name,last_name,blocked_at,banned_at) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)", DbValue.Uuid(row.TargetUser), DbValue.Uuid(context.BrandId), DbValue.Bigint(row.TelegramId), DbValue.Text(row.Language), DbValue.Utc(row.FirstSeen), DbValue.Utc(row.LastSeen), LegacyValues.String(row.State?.Username), LegacyValues.String(row.State?.FirstName), LegacyValues.String(row.State?.LastName), LegacyValues.Date(row.State is { Active: false } ? row.State.ObservedAt : null), LegacyValues.Date(row.State is { Banned: true } ? row.State.BannedAt ?? row.State.ObservedAt : null)), ct);
        await session.QuerySingleAsync(new DbStatement("SELECT id FROM shop.users WHERE id=$1 AND brand_id=$2 AND telegram_id=$3 FOR UPDATE", DbValue.Uuid(row.TargetUser), DbValue.Uuid(context.BrandId), DbValue.Bigint(row.TelegramId)), r => r.GetGuid(0), ct);
        bool first = await LegacyMapStore.EnsureAsync(session, manifest, proofs.Single(p => p.Projection == LegacyProjection.WalletSnapshot), ct);
        if (first != newUser) throw new MigrationRejectedException();
        if (row.BalanceUsd > 0)
        {
            bool opening = await LegacyMapStore.EnsureAsync(session, manifest, proofs.Single(p => p.Projection == LegacyProjection.WalletOpening), ct);
            if (opening != first) throw new MigrationRejectedException();
        }
        if (first)
        {
            await session.ExecuteAsync(new DbStatement("INSERT INTO shop.wallets(user_id) VALUES ($1)", DbValue.Uuid(row.TargetUser)), ct);
            if (row.BalanceUsd > 0)
            {
                await session.ExecuteAsync(new DbStatement("UPDATE shop.wallets SET balance=$1,version=1 WHERE user_id=$2", DbValue.Numeric(row.BalanceUsd), DbValue.Uuid(row.TargetUser)), ct);
                await session.ExecuteAsync(new DbStatement("INSERT INTO shop.wallet_entries(id,user_id,signed_amount,balance_after,posting_version,movement_kind,is_posting,idempotency_key,legacy_import_batch,reason) VALUES ($1,$2,$3,$3,1,'opening_balance',true,$4,$5,'Frozen authoritative USD opening; history is non-posting')",
                    DbValue.Uuid(row.TargetOpening), DbValue.Uuid(row.TargetUser), DbValue.Numeric(row.BalanceUsd), DbValue.Text("opening:" + row.TargetOpening.ToString("D")), DbValue.Uuid(manifest.Batch)), ct);
            }
            await session.ExecuteAsync(new DbStatement("INSERT INTO shop.audit_events(id,actor_kind,brand_id,action,entity_kind,entity_id,reason,correlation_id,command_sha256) VALUES ($1,'system',$2,'legacy_import','wallet',$3,$4,$5,$6)", DbValue.Uuid(Guid.NewGuid()), DbValue.Uuid(context.BrandId), DbValue.Uuid(row.TargetUser), DbValue.Text(row.Retention?.Reference ?? "Frozen manifest-bound USD opening; no financial history replay"), DbValue.Uuid(manifest.Batch), row.Retention is null ? DbValue.Null(NpgsqlDbType.Bytea) : DbValue.Binary(row.Retention.Digest.Span)), ct);
        }
        foreach (var history in row.History)
        {
            var proof = proofs.Single(p => p.Projection == LegacyProjection.Transaction && p.LegacyId == history.LegacyId);
            if (await LegacyMapStore.EnsureAsync(session, manifest, proof, ct))
                await session.ExecuteAsync(new DbStatement("INSERT INTO shop.wallet_entries(id,user_id,signed_amount,balance_after,movement_kind,is_posting,idempotency_key,legacy_import_batch,reason,created_at) VALUES ($1,$2,$3,$4,'legacy_history',false,$5,$6,'Non-posting source evidence; see protected frozen snapshot',$7)",
                    DbValue.Uuid(history.TargetEntry), DbValue.Uuid(row.TargetUser), DbValue.Numeric(history.SignedAmount), history.BalanceAfter is decimal amount ? DbValue.Numeric(amount) : DbValue.Null(NpgsqlDbType.Numeric), DbValue.Text("history:" + history.TargetEntry.ToString("D")), DbValue.Uuid(manifest.Batch), DbValue.Utc(history.OccurredAt)), ct);
            if (!await MatchesHistoryAsync(session, manifest.Batch, row.TargetUser, history, ct)) throw new MigrationRejectedException();
        }
        if (!await MatchesWalletAsync(session, manifest, row, ct)) throw new MigrationRejectedException();
        await session.CommitAsync(ct);

    }
    internal static async Task<bool> MatchesWalletAsync(DbSession session, VerifiedImportManifest manifest, LegacyWalletSnapshot row, CancellationToken ct)
    {
        if (row.Retention is not null && !await session.QuerySingleAsync(new DbStatement("SELECT EXISTS(SELECT 1 FROM shop.audit_events WHERE actor_kind='system' AND brand_id=$1 AND action='legacy_import' AND entity_kind='wallet' AND entity_id=$2 AND correlation_id=$3 AND command_sha256=$4 AND reason=$5)", DbValue.Uuid(manifest.TargetBrand), DbValue.Uuid(row.TargetUser), DbValue.Uuid(manifest.Batch), DbValue.Binary(row.Retention.Digest.Span), DbValue.Text(row.Retention.Reference)), r => r.GetBoolean(0), ct)) return false;
        bool member = await session.QuerySingleAsync(new DbStatement("SELECT EXISTS(SELECT 1 FROM shop.users u JOIN shop.wallets w ON w.user_id=u.id WHERE u.id=$1 AND u.brand_id=$2 AND u.telegram_id=$3 AND u.language=$4 AND u.first_seen_at=$5 AND u.last_seen_at=$6 AND w.balance=$7 AND w.version=$8)", DbValue.Uuid(row.TargetUser), DbValue.Uuid(manifest.TargetBrand), DbValue.Bigint(row.TelegramId), DbValue.Text(row.Language), DbValue.Utc(row.FirstSeen), DbValue.Utc(row.LastSeen), DbValue.Numeric(row.BalanceUsd), DbValue.Bigint(row.BalanceUsd > 0 ? 1 : 0)), r => r.GetBoolean(0), ct);
        if (member) member = await session.QuerySingleAsync(new DbStatement("SELECT EXISTS(SELECT 1 FROM shop.users WHERE id=$1 AND username IS NOT DISTINCT FROM $2 AND first_name IS NOT DISTINCT FROM $3 AND last_name IS NOT DISTINCT FROM $4 AND blocked_at IS NOT DISTINCT FROM $5 AND banned_at IS NOT DISTINCT FROM $6)", DbValue.Uuid(row.TargetUser), LegacyValues.String(row.State?.Username), LegacyValues.String(row.State?.FirstName), LegacyValues.String(row.State?.LastName), LegacyValues.Date(row.State is { Active: false } ? row.State.ObservedAt : null), LegacyValues.Date(row.State is { Banned: true } ? row.State.BannedAt ?? row.State.ObservedAt : null)), r => r.GetBoolean(0), ct);
        if (!member || row.BalanceUsd == 0) return member;
        return await session.QuerySingleAsync(new DbStatement("SELECT EXISTS(SELECT 1 FROM shop.wallet_entries WHERE id=$1 AND user_id=$2 AND signed_amount=$3 AND balance_after=$3 AND posting_version=1 AND movement_kind='opening_balance' AND is_posting AND legacy_import_batch=$4 AND idempotency_key=$5)", DbValue.Uuid(row.TargetOpening), DbValue.Uuid(row.TargetUser), DbValue.Numeric(row.BalanceUsd), DbValue.Uuid(manifest.Batch), DbValue.Text("opening:" + row.TargetOpening.ToString("D"))), r => r.GetBoolean(0), ct);
    }
    internal static Task<bool> MatchesHistoryAsync(DbSession session, Guid batch, Guid user, LegacyHistoryRow history, CancellationToken ct) =>
        session.QuerySingleAsync(new DbStatement("SELECT EXISTS(SELECT 1 FROM shop.wallet_entries WHERE id=$1 AND user_id=$2 AND signed_amount=$3 AND balance_after IS NOT DISTINCT FROM $4 AND movement_kind='legacy_history' AND NOT is_posting AND posting_version IS NULL AND legacy_import_batch=$5 AND created_at=$6 AND idempotency_key=$7)", DbValue.Uuid(history.TargetEntry), DbValue.Uuid(user), DbValue.Numeric(history.SignedAmount), history.BalanceAfter is decimal amount ? DbValue.Numeric(amount) : DbValue.Null(NpgsqlDbType.Numeric), DbValue.Uuid(batch), DbValue.Utc(history.OccurredAt), DbValue.Text("history:" + history.TargetEntry.ToString("D"))), r => r.GetBoolean(0), ct);
}
