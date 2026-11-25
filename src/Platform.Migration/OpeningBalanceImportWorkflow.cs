using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using Platform.Persistence.Entities;
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
        bool brand = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Brand>().AnyAsync(row => row.Id == context.BrandId && row.BotId == context.BotId, token), ct);
        if (!brand) throw new MigrationRejectedException();
        bool newUser = await LegacyMapStore.EnsureAsync(session, manifest, proofs.Single(p => p.Projection == LegacyProjection.User), ct);
        if (newUser) await session.RunEfStepAsync(true, (data, token) => { data.RequireImportCommand(); data.Data.Add(new User { Id = row.TargetUser, BrandId = context.BrandId, TelegramId = row.TelegramId, Language = row.Language, FirstSeenAt = row.FirstSeen, LastSeenAt = row.LastSeen, Username = row.State?.Username, FirstName = row.State?.FirstName, LastName = row.State?.LastName, BlockedAt = row.State is { Active: false } ? row.State.ObservedAt : null, BannedAt = row.State is { Banned: true } ? row.State.BannedAt ?? row.State.ObservedAt : null }); return Task.FromResult(true); }, ct);
        await session.RunEfStepAsync(false, async (data, token) => { await data.LockUserAsync(row.TargetUser, context.BrandId, token); return await data.Data.Set<User>().Where(value => value.Id == row.TargetUser && value.BrandId == context.BrandId && value.TelegramId == row.TelegramId).Select(value => value.Id).SingleAsync(token); }, ct);
        bool first = await LegacyMapStore.EnsureAsync(session, manifest, proofs.Single(p => p.Projection == LegacyProjection.WalletSnapshot), ct);
        if (first != newUser) throw new MigrationRejectedException();
        if (row.BalanceUsd > 0)
        {
            bool opening = await LegacyMapStore.EnsureAsync(session, manifest, proofs.Single(p => p.Projection == LegacyProjection.WalletOpening), ct);
            if (opening != first) throw new MigrationRejectedException();
        }
        if (first)
        {
            await session.RunEfStepAsync(true, async (data, token) =>
            {
                await data.RequireWalletCommandAsync(row.TargetUser, token);
                if (await data.Data.Set<Platform.Persistence.Entities.Wallet>().AnyAsync(value => value.UserId == row.TargetUser, token)) data.RejectScope("Opening wallet already exists.");
                data.Data.Add(new Platform.Persistence.Entities.Wallet { UserId = row.TargetUser });
                await data.FlushAsync(token, false);
                if (row.BalanceUsd > 0) await WalletOperations.PostAsync(data, row.TargetUser, new Money(row.BalanceUsd, Currency.Usd), 0,
                    new Platform.Persistence.Entities.WalletEntry { Id = row.TargetOpening, MovementKind = "opening_balance", IdempotencyKey = "opening:" + row.TargetOpening.ToString("D"), LegacyImportBatch = manifest.Batch, Reason = "Frozen authoritative USD opening; history is non-posting" }, token);
                return true;
            }, ct);
            byte[]? retentionDigest = row.Retention?.Digest.ToArray();
            await session.RunEfStepAsync(true, (data, token) => { data.RequireImportCommand(); data.Data.Add(new AuditEvent { Id = Guid.NewGuid(), ActorKind = "system", BrandId = context.BrandId, Action = "legacy_import", EntityKind = "wallet", EntityId = row.TargetUser, Reason = row.Retention?.Reference ?? "Frozen manifest-bound USD opening; no financial history replay", CorrelationId = manifest.Batch, CommandSha256 = retentionDigest }); return Task.FromResult(true); }, ct);
        }
        foreach (var history in row.History)
        {
            var proof = proofs.Single(p => p.Projection == LegacyProjection.Transaction && p.LegacyId == history.LegacyId);
            if (await LegacyMapStore.EnsureAsync(session, manifest, proof, ct))
                await session.RunEfStepAsync(true, async (data, token) =>
                {
                    await data.RequireWalletCommandAsync(row.TargetUser, token);
                    data.Data.Add(new Platform.Persistence.Entities.WalletEntry { Id = history.TargetEntry, UserId = row.TargetUser, SignedAmount = history.SignedAmount, BalanceAfter = history.BalanceAfter, MovementKind = "legacy_history", IsPosting = false, IdempotencyKey = "history:" + history.TargetEntry.ToString("D"), LegacyImportBatch = manifest.Batch, Reason = "Non-posting source evidence; see protected frozen snapshot", CreatedAt = history.OccurredAt });
                    return true;
                }, ct);
            if (!await MatchesHistoryAsync(session, manifest.Batch, row.TargetUser, history, ct)) throw new MigrationRejectedException();
        }
        if (!await MatchesWalletAsync(session, manifest, row, ct)) throw new MigrationRejectedException();
        await session.CommitAsync(ct);

    }
    internal static async Task<bool> MatchesWalletAsync(DbSession session, VerifiedImportManifest manifest, LegacyWalletSnapshot row, CancellationToken ct)
    {
        if (row.Retention is not null)
        {
            byte[] digest = row.Retention.Digest.ToArray();
            if (!await session.RunEfStepAsync(false, (data, token) => data.Data.Set<AuditEvent>().AnyAsync(a => a.ActorKind == "system" && a.BrandId == manifest.TargetBrand && a.Action == "legacy_import" && a.EntityKind == "wallet" && a.EntityId == row.TargetUser && a.CorrelationId == manifest.Batch && a.CommandSha256 == digest && a.Reason == row.Retention.Reference, token), ct)) return false;
        }
        bool member = await session.RunEfStepAsync(false, (data, token) => (from u in data.Data.Set<User>() join w in data.Data.Set<Wallet>() on u.Id equals w.UserId where u.Id == row.TargetUser && u.BrandId == manifest.TargetBrand && u.TelegramId == row.TelegramId && u.Language == row.Language && u.FirstSeenAt == row.FirstSeen && u.LastSeenAt == row.LastSeen && w.Balance == row.BalanceUsd && w.Version == (row.BalanceUsd > 0 ? 1 : 0) select u.Id).AnyAsync(token), ct);
        string? username = row.State?.Username, first = row.State?.FirstName, last = row.State?.LastName; DateTime? blocked = row.State is { Active: false } ? row.State.ObservedAt : null, banned = row.State is { Banned: true } ? row.State.BannedAt ?? row.State.ObservedAt : null;
        if (member) member = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<User>().AnyAsync(u => u.Id == row.TargetUser && u.Username == username && u.FirstName == first && u.LastName == last && u.BlockedAt == blocked && u.BannedAt == banned, token), ct);
        if (!member || row.BalanceUsd == 0) return member;
        string opening = "opening:" + row.TargetOpening.ToString("D");
        return await session.RunEfStepAsync(false, (data, token) => data.Data.Set<WalletEntry>().AnyAsync(w => w.Id == row.TargetOpening && w.UserId == row.TargetUser && w.SignedAmount == row.BalanceUsd && w.BalanceAfter == row.BalanceUsd && w.PostingVersion == 1 && w.MovementKind == "opening_balance" && w.IsPosting && w.LegacyImportBatch == manifest.Batch && w.IdempotencyKey == opening, token), ct);
    }
    internal static Task<bool> MatchesHistoryAsync(DbSession session, Guid batch, Guid user, LegacyHistoryRow history, CancellationToken ct)
    {
        string key = "history:" + history.TargetEntry.ToString("D");
        return session.RunEfStepAsync(false, (data, token) => data.Data.Set<WalletEntry>().AnyAsync(w => w.Id == history.TargetEntry && w.UserId == user && w.SignedAmount == history.SignedAmount && w.BalanceAfter == history.BalanceAfter && w.MovementKind == "legacy_history" && !w.IsPosting && w.PostingVersion == null && w.LegacyImportBatch == batch && w.CreatedAt == history.OccurredAt && w.IdempotencyKey == key, token), ct);
    }
}
