using System.Security.Cryptography;
using System.Text.Json;
using Platform.Migration;
namespace Platform.SchemaRunner;

internal static class ReadOnlyUnifiedReconciliationCommand
{
    internal static async Task RunAsync(string[] jobs, string destinationFile, string targetFile, string backupFile, string fingerprintsFile, string checkpoints, string receipt, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("SHOP_ALLOW_PROTECTED_RECONCILIATION") != "1" || Environment.GetEnvironmentVariable("SHOP_ALLOW_PROTECTED_SOURCE_INSPECTION") != "1" || jobs.Length != 3 || jobs.Distinct(StringComparer.Ordinal).Count() != 3 || !Path.IsPathFullyQualified(receipt) || File.Exists(receipt) || !receipt.EndsWith(".receipt.json", StringComparison.Ordinal) || !Path.IsPathFullyQualified(checkpoints) || !Directory.Exists(checkpoints) || new DirectoryInfo(checkpoints).LinkTarget is not null || (File.GetUnixFileMode(checkpoints) & (UnixFileMode)63) != 0) throw new MigrationRejectedException();
        string? output = Path.GetDirectoryName(receipt);
        if (output is null || !Directory.Exists(output) || new DirectoryInfo(output).LinkTarget is not null || (File.GetUnixFileMode(output) & (UnixFileMode)63) != 0) throw new MigrationRejectedException();
        string connection = ArchivePrivateFile.ReadText(targetFile).Trim(), backup = ArchivePrivateFile.ReadText(backupFile).Trim(), fingerprints = ArchivePrivateFile.ReadText(fingerprintsFile);
        await ReadOnlyTargetFingerprints.VerifyAsync(connection, backup, fingerprints, ct);
        string[] names = ["acc-abad", "kuala", "zikotech"]; var results = new List<RedactedLegacySnapshotBrand>(); var identities = new HashSet<Guid>();
        for (int i = 0; i < jobs.Length; i++)
        {
            string job = ArchivePrivateFile.ReadText(jobs[i]); using var document = JsonDocument.Parse(job);
            if (output == document.RootElement.GetProperty("work_directory").GetString()) throw new MigrationRejectedException();
            string directory = Path.Combine(checkpoints, "teleg-reconcile-checkpoints-prod" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (!Directory.Exists(directory) || new DirectoryInfo(directory).LinkTarget is not null || (File.GetUnixFileMode(directory) & (UnixFileMode)63) != 0) throw new MigrationRejectedException();
            ReadOnlyTargetEvidence? target = null;
            var source = await ReadOnlySourceArchiveCommand.VerifyJobAsync(job, directory, ArchivePrivateFile.ReadText, ct, async (reader, manifest, context, secrets, token) =>
            {
                if (manifest.SourceBrand != names[i] || !identities.Add(context.BrandId)) throw new MigrationRejectedException();
                target = await ReadOnlyTargetReconciliation.VerifyAsync(reader, manifest, context, secrets, job, destinationFile, targetFile, backupFile, ArchivePrivateFile.ReadText, token);
            });
            if (target is null || target.SourceBrand != names[i] || target.FinancialObligationsVerified || target.ReleaseReady || target.Disposition.ReleaseReady) throw new MigrationRejectedException();
            var d = target.Disposition;
            results.Add(new(target.SourceBrand, source.SourceTables, source.SourceRows, source.OriginalArchiveRecords, source.RecoveredFields, target.Projection, d.Archived, d.Orders, d.Payments, d.NativeZero, d.Catalog, d.Reviewed, target.DerivedZeroProofs, d.ReviewReasons));
        }
        await ReadOnlyTargetFingerprints.VerifyAsync(connection, backup, fingerprints, ct);
        var report = ReconciliationReport.RedactSnapshot(results);
        if (!report.PreservationMatched || report.FinancialObligationsVerified || report.ReleaseReady || report.MediaAvailabilityVerified) throw new MigrationRejectedException();
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(report, MigrationReportJsonContext.Default.RedactedLegacySnapshotReconciliation);
        try { await WalletCopyQualification.WriteAsync(receipt, body, ct); } finally { CryptographicOperations.ZeroMemory(body); }
    }
}
