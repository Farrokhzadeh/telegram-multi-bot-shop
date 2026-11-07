using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Platform.Core;
namespace Platform.Migration;

public sealed class LegacyWalletRetention
{
    private readonly byte[] digest;
    private readonly Dictionary<string, LegacyRetainedTransaction> history;
    private LegacyWalletRetention(string reference, byte[] digest, string source, string archive, string user, decimal balance, Dictionary<string, LegacyRetainedTransaction> history)
    { Reference = reference; this.digest = digest; SourceBrand = source; ArchiveDigest = archive; LegacyUser = user; OriginalIrr = balance; this.history = history; }
    public string Reference { get; }
    public ReadOnlyMemory<byte> Digest => digest;
    public string SourceBrand { get; }
    public string ArchiveDigest { get; }
    internal string LegacyUser { get; }
    internal decimal OriginalIrr { get; }
    public long HistoryRows => history.Count;
    public long IrrHistoryRows => history.Values.LongCount(r => r.Currency == "IRR");
    public long ZeroHistoryRows => history.Values.LongCount(r => r.Amount == 0);
    public long ProjectedUsdHistoryRows => history.Values.LongCount(r => r.Currency == "USD" && r.Amount != 0);
    public bool ManualIrrBalance => OriginalIrr != 0;
    public static LegacyWalletRetention Open(string reference, ReadOnlySpan<byte> expectedDigest, ReadOnlySpan<byte> body, string sourceBrand, string archiveDigest, string legacyUser)
    {
        var wallets = OpenAll(reference, expectedDigest, body, sourceBrand, archiveDigest);
        return wallets.TryGetValue(legacyUser, out var wallet) ? wallet : throw new MigrationRejectedException();
    }
    public static IReadOnlyDictionary<string, LegacyWalletRetention> OpenAll(string reference, ReadOnlySpan<byte> expectedDigest, ReadOnlySpan<byte> body, string sourceBrand, string archiveDigest)
    {
        if (reference.Length is < 11 or > 512 || !reference.StartsWith("protected:", StringComparison.Ordinal) || reference.Any(char.IsControl) || expectedDigest.Length != 32 || body.Length is < 128 or > 67108864 || !CryptographicOperations.FixedTimeEquals(expectedDigest, SHA256.HashData(body)) || archiveDigest.Length != 64 || !archiveDigest.All(char.IsAsciiHexDigit)) throw new MigrationRejectedException();
        try
        {
            using var document = JsonDocument.Parse(body.ToArray()); var root = document.RootElement; RequireUnique(root);
            if (root.GetProperty("version").GetInt32() != 2 || root.GetProperty("policy").GetString() != "manual_irr_and_original_nonposting_history_v2") throw new MigrationRejectedException();
            var sources = root.GetProperty("sources").EnumerateArray().ToArray();
            if (sources.Length != 3 || sources.Select(s => s.GetProperty("source").GetString()).Distinct().Count() != 3 || sources.Select(s => s.GetProperty("brand").GetString()).Distinct().Count() != 3 || sources.Any(s => (s.GetProperty("source").GetString(), s.GetProperty("brand").GetString()) is not (("prod1", "acc-abad") or ("prod2", "kuala") or ("prod3", "zikotech")))) throw new MigrationRejectedException();
            var source = sources.Single(s => s.GetProperty("brand").GetString() == sourceBrand);
            if (source.GetProperty("archive_sha256").GetString() != archiveDigest) throw new MigrationRejectedException();
            byte[] digest = expectedDigest.ToArray(); var wallets = new Dictionary<string, LegacyWalletRetention>(StringComparer.Ordinal);
            foreach (var wallet in source.GetProperty("wallets").EnumerateArray())
            {
                string user = Identity(wallet.GetProperty("legacy_user_id").GetString());
                if (!wallets.TryAdd(user, new(reference, digest, sourceBrand, archiveDigest, user, ExactDecimal.Parse(wallet.GetProperty("original_irr_amount").GetString()!), new(StringComparer.Ordinal)))) throw new MigrationRejectedException();
            }
            if (wallets.Count != source.GetProperty("source_users").GetInt32()) throw new MigrationRejectedException();
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in source.GetProperty("history").EnumerateArray())
            {
                string id = row.GetProperty("id").GetString() ?? throw new MigrationRejectedException();
                if (id.Length is < 1 or > 256 || id.Any(char.IsControl) || !identities.Add(id) || !wallets.TryGetValue(Identity(row.GetProperty("user_id").GetString()), out var wallet)) throw new MigrationRejectedException();
                string currency = row.GetProperty("currency").GetString() ?? throw new MigrationRejectedException();
                DateTimeOffset created = DateTimeOffset.Parse(row.GetProperty("created_at").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.None);
                string clock = row.GetProperty("created_at").GetString()!;
                if (currency is not ("USD" or "IRR") || !(clock.EndsWith("Z", StringComparison.Ordinal) || clock.EndsWith("+00", StringComparison.Ordinal) || clock.EndsWith("+00:00", StringComparison.Ordinal)) || created.Offset != TimeSpan.Zero || created.UtcDateTime.Ticks % 10 != 0 || created == DateTimeOffset.MinValue || created == DateTimeOffset.MaxValue) throw new MigrationRejectedException();
                wallet.history.Add(id, new(id, ExactDecimal.Parse(row.GetProperty("amount").GetString()!), currency, created.UtcDateTime));
            }
            return new System.Collections.ObjectModel.ReadOnlyDictionary<string, LegacyWalletRetention>(wallets);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArithmeticException or ArgumentException) { throw new MigrationRejectedException(); }
    }
    private static string Identity(string? value) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long id) && id > 0 && id.ToString(CultureInfo.InvariantCulture) == value ? value! : throw new MigrationRejectedException();
    private static void RequireUnique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject()) { if (!fields.Add(field.Name)) throw new MigrationRejectedException(); RequireUnique(field.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RequireUnique(item);
    }
    internal void Require(VerifiedImportManifest manifest)
    {
        if (SourceBrand != manifest.SourceBrand || ArchiveDigest != manifest.SnapshotDigest || manifest.SourceRevision != "66371a23a1c8149dc5bdf89a0355aa689d454dba") throw new MigrationRejectedException();
    }
    internal void RequireRow(string user, decimal other, IReadOnlyList<LegacyHistoryRow> rows)
    {
        if (LegacyUser != user || OriginalIrr != other || rows.Count != history.Values.Count(r => r.Currency == "USD" && r.Amount != 0) || rows.Any(r => !history.TryGetValue(r.LegacyId, out var h) || r.Currency != "USD" || r.SignedAmount == 0 || r.BalanceAfter is not null || h != new LegacyRetainedTransaction(r.LegacyId, r.SignedAmount, r.Currency, r.OccurredAt))) throw new MigrationRejectedException();
    }
    internal void RequireHistory(IReadOnlyList<LegacyRetainedTransaction> rows)
    {
        if (rows.Count != history.Count || rows.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != rows.Count || rows.Any(r => !history.TryGetValue(r.Id, out var expected) || expected != r)) throw new MigrationRejectedException();
    }
    internal void RequireZero(LegacySourceRecord source)
    {
        using var document = source.Open(); var row = document.RootElement;
        if (source.Projection != LegacyProjection.ZeroTransaction || row.GetProperty("user_id").GetInt64().ToString(CultureInfo.InvariantCulture) != LegacyUser || LegacyValues.Text(row, "currency") != "USD" || !history.TryGetValue(source.Id, out var retained) || retained != new LegacyRetainedTransaction(source.Id, 0, "USD", LegacyValues.Time(row, "created_at"))) throw new MigrationRejectedException();
    }
    public bool Matches(ReadOnlySpan<byte> body) => CryptographicOperations.FixedTimeEquals(digest, SHA256.HashData(body));
    internal async Task<bool> MatchesAsync(Func<string, CancellationToken, Task<ReadOnlyMemory<byte>>>? read, CancellationToken ct)
    {
        if (read is null) return false;
        try { return Matches((await read(Reference, ct)).Span); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or KeyNotFoundException) { return false; }
    }
    public override string ToString() => "LegacyWalletRetention [redacted]";
}
internal sealed record LegacyRetainedTransaction(string Id, decimal Amount, string Currency, DateTime Created)
{
    public override string ToString() => "LegacyRetainedTransaction [redacted]";
}

internal sealed class WalletRetentionVerification
{
    private readonly HashSet<(string Reference, string Digest)> valid = [];
    private WalletRetentionVerification() { }
    private static (string, string) Key(LegacyWalletRetention row) => (row.Reference, Convert.ToHexStringLower(row.Digest.Span));
    internal bool Matches(LegacyWalletRetention row) => valid.Contains(Key(row));
    internal static async Task<WalletRetentionVerification> ReadAsync(IEnumerable<LegacyWalletRetention> rows, Func<string, CancellationToken, Task<ReadOnlyMemory<byte>>>? read, CancellationToken ct)
    {
        var result = new WalletRetentionVerification(); var seen = new HashSet<(string, string)>();
        foreach (var row in rows) if (seen.Add(Key(row)) && await row.MatchesAsync(read, ct)) result.valid.Add(Key(row));
        return result;
    }
}
