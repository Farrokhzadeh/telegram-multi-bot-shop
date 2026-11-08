using System.Globalization;
using System.Security.Cryptography;
using Platform.Core;
using Platform.Persistence;
using Platform.Security;
namespace Platform.Migration;

public sealed record LegacyRecordReview(LegacyProjection Projection, string LegacyId, Guid Target, string Reason)
{
    public override string ToString() => "LegacyRecordReview [redacted]";
}
public sealed class LegacyWalletOrderPlan
{
    private LegacyWalletOrderPlan(VerifiedImportManifest archive, VerifiedImportManifest manifest, LegacyImportSet set, ArchivedLegacyRecord[] records, LegacyRecordReview[] review)
    { Archive = archive; Manifest = manifest; Set = set; Records = Array.AsReadOnly(records); Review = Array.AsReadOnly(review); }
    public VerifiedImportManifest Archive { get; }
    public VerifiedImportManifest Manifest { get; }
    public LegacyImportSet Set { get; }
    public IReadOnlyList<ArchivedLegacyRecord> Records { get; }
    public IReadOnlyList<LegacyRecordReview> Review { get; }
    public bool ReleaseReady => false;
    public static LegacyWalletOrderPlan Create(BrandContext context, SecretSnapshot secrets, VerifiedImportManifest archive, VerifiedImportManifest walletManifest, SchemaManifest destination, string reference, string historicalBrandName, IReadOnlyList<LegacyWalletSnapshot> input, IReadOnlyList<ArchivedLegacyRecord> archived) => Build(context, secrets, archive, walletManifest, destination, reference, historicalBrandName, input, archived, false, false);
    public static async Task<LegacyWalletOrderPlan> CreateWithZeroOrdersAsync(BrandContext context, SecretSnapshot secrets, VerifiedImportManifest archive, VerifiedImportManifest walletManifest, SchemaManifest destination, string reference, string historicalBrandName, IReadOnlyList<LegacyWalletSnapshot> input, IReadOnlyList<ArchivedLegacyRecord> archived, Func<VerifiedImportManifest, LegacySourceRecord, Guid, CancellationToken, Task<ArchivedLegacyRecord>> archiveZero, CancellationToken ct = default, bool walletTopups = false)
    {
        var plan = Build(context, secrets, archive, walletManifest, destination, reference, historicalBrandName, input, archived, true, walletTopups); var zeroRows = new List<ArchivedLegacyRecord>(); var protector = new LegacyRecordArchive(secrets);
        foreach (var order in plan.Set.Orders.Where(o => o.NativeZeroSource is not null))
        {
            var row = await archiveZero(plan.Manifest, order.NativeZeroSource!, order.Record.Target, ct);
            if (row.Source != order.NativeZeroSource || row.Target != order.Record.Target || !protector.Matches(context, row, row.Evidence)) throw new MigrationRejectedException();
            plan.Manifest.Require(row.Source.Proof(row.Target, context, secrets)); zeroRows.Add(row);
        }
        return new(plan.Archive, plan.Manifest, plan.Set with { Delivery = zeroRows.AsReadOnly() }, plan.Records.ToArray(), plan.Review.ToArray());
    }
    private static LegacyWalletOrderPlan Build(BrandContext context, SecretSnapshot secrets, VerifiedImportManifest archive, VerifiedImportManifest walletManifest, SchemaManifest destination, string reference, string historicalBrandName, IReadOnlyList<LegacyWalletSnapshot> input, IReadOnlyList<ArchivedLegacyRecord> archived, bool zeroOrders, bool walletTopups)
    {
        if (context.BrandId != archive.TargetBrand || archive.TargetBrand != walletManifest.TargetBrand || archive.SourceBrand != walletManifest.SourceBrand || archive.SourceRevision != walletManifest.SourceRevision || archive.SourceSchemaDigest != walletManifest.SourceSchemaDigest || archive.SnapshotDigest != walletManifest.SnapshotDigest || archive.SnapshotReference != walletManifest.SnapshotReference || string.IsNullOrWhiteSpace(historicalBrandName) || historicalBrandName.Length > 160 || historicalBrandName.Any(char.IsControl)) throw new MigrationRejectedException();
        var wallets = input.Select(w => w with { History = Array.AsReadOnly(w.History.ToArray()) }).ToArray();
        var records = archived.Select(r => new ArchivedLegacyRecord(r.Source, r.Target, r.Reference, r.Evidence)).ToArray();
        if (wallets.Length == 0 || wallets.Select(w => w.LegacyUser).Distinct(StringComparer.Ordinal).Count() != wallets.Length || wallets.Select(w => w.TelegramId).Distinct().Count() != wallets.Length || wallets.Select(w => w.TargetUser).Distinct().Count() != wallets.Length || records.Select(r => (r.Source.Projection, r.Source.Id)).Distinct().Count() != records.Length || records.Length != archive.Proofs.Count(p => LegacySourceRecord.Projections.Contains(p.Projection)) || archive.Proofs.Any(p => p.Projection != LegacyProjection.User && !LegacySourceRecord.Projections.Contains(p.Projection)) || wallets.Length != archive.Proofs.Count(p => p.Projection == LegacyProjection.User) || walletManifest.Proofs.Any(p => p.Projection is not (LegacyProjection.User or LegacyProjection.WalletSnapshot or LegacyProjection.WalletOpening or LegacyProjection.Transaction)) || wallets.Sum(w => w.Proofs(context.BrandId).Count) != walletManifest.Proofs.Count || wallets.Sum(w => w.BalanceUsd) != walletManifest.ExpectedOpeningUsd) throw new MigrationRejectedException();
        var protector = new LegacyRecordArchive(secrets);
        foreach (var wallet in wallets)
        {
            if (wallet.Retention is null || wallet.LegacyUser != wallet.TelegramId.ToString(CultureInfo.InvariantCulture)) throw new MigrationRejectedException();
            wallet.Retention.Require(walletManifest);
            foreach (var proof in wallet.Proofs(context.BrandId)) { walletManifest.Require(proof); if (proof.Projection == LegacyProjection.User) archive.Require(proof); }
        }
        foreach (var row in records)
        {
            archive.Require(row.Source.Proof(row.Target, context, secrets));
            if (!protector.Matches(context, row, row.Evidence)) throw new MigrationRejectedException();
        }
        var owners = wallets.ToDictionary(w => w.TelegramId); var orders = new List<LegacyHistoricalRecord>(); var payments = new List<LegacyPaymentRecord>(); var review = new List<LegacyRecordReview>();
        var zeros = records.Where(r => r.Source.Projection == LegacyProjection.ZeroTransaction).ToDictionary(r => r.Source.Id, r => r.Source, StringComparer.Ordinal); var selectedZeros = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in records.OrderBy(r => r.Source.Projection).ThenBy(r => r.Source.Id, StringComparer.Ordinal))
        {
            if (row.Source.Projection == LegacyProjection.ZeroTransaction && selectedZeros.Contains(row.Source.Id)) continue;
            string reason = "outside_wallet_order_scope";
            if (row.Source.Projection == LegacyProjection.OrderRecord)
            {
                using var original = row.Source.Open();
                if (!owners.ContainsKey(original.RootElement.GetProperty("user_id").GetInt64())) throw new MigrationRejectedException();
                LegacyOrderTerms? terms = null;
                try { terms = LegacyHistoricalRecord.ReadTerms(row.Source); } catch (MigrationRejectedException) { reason = "incomplete_order_terms"; }
                if (terms is not null)
                {
                    if (!owners.TryGetValue(terms.Telegram, out var owner)) throw new MigrationRejectedException();
                    if (terms.Free)
                    {
                        reason = zeroOrders ? "missing_exact_native_zero_transaction" : "zero_order_requires_separate_proof";
                        if (zeroOrders && zeros.TryGetValue(row.Source.Id, out var zero))
                        {
                            try { owner.Retention!.RequireZero(zero); var order = new LegacyHistoricalRecord(row, owner.TargetUser, historicalBrandName, NativeZeroSource: zero); _ = order.Terms(); orders.Add(order); selectedZeros.Add(zero.Id); continue; }
                            catch (MigrationRejectedException) { }
                        }
                    }
                    else if (terms.Method == "wallet" && terms.Currency == "USDT")
                    {
                        decimal amount = ExactDecimal.Multiply(terms.UnitUsd, terms.Quantity);
                        var debit = owner.History.SingleOrDefault(h => h.LegacyId == row.Source.Id && h.Currency == "USD" && h.SignedAmount == -amount);
                        if (debit is not null) { orders.Add(new(row, owner.TargetUser, historicalBrandName, debit.OccurredAt)); continue; }
                        reason = "missing_exact_native_wallet_debit";
                    }
                }
            }
            if (walletTopups && row.Source.Projection == LegacyProjection.PaymentAttempt)
            {
                using var original = row.Source.Open();
                if (!owners.TryGetValue(original.RootElement.GetProperty("user_id").GetInt64(), out var owner)) throw new MigrationRejectedException();
                var payment = new LegacyPaymentRecord(row, owner.TargetUser, null);
                try { _ = payment.Terms(walletManifest); payments.Add(payment); continue; }
                catch (MigrationRejectedException) { reason = "payment_terms_require_review"; }
            }
            review.Add(new(row.Source.Projection, row.Source.Id, row.Target, reason));
        }
        var proofs = wallets.SelectMany(w => w.Proofs(context.BrandId)).Concat(orders.SelectMany(o => o.Proofs(context, secrets))).Concat(orders.Where(o => o.NativeZeroSource is not null).Select(o => o.NativeZeroSource!.Proof(o.Record.Target, context, secrets))).Concat(payments.Select(p => p.Record.Source.Proof(p.Record.Target, context, secrets)));
        byte[] body = VerifiedImportManifest.EncodePlan(destination, walletManifest.SourceBrand, context.BrandId, walletManifest.SourceRevision, Convert.FromHexString(walletManifest.SourceSchemaDigest), walletManifest.SnapshotReference, Convert.FromHexString(walletManifest.SnapshotDigest), walletManifest.Batch, proofs, walletManifest.ExpectedOpeningUsd);
        var manifest = VerifiedImportManifest.Open(reference, SHA256.HashData(body), body);
        return new(archive, manifest, new(Array.AsReadOnly(wallets), orders.AsReadOnly(), payments.AsReadOnly(), [], [], []), records, review.ToArray());
    }
    public override string ToString() => "LegacyWalletOrderPlan [redacted]";
}
