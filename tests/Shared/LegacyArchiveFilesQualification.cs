using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Platform.Migration;
using Platform.Persistence;
using Platform.Security;
namespace Platform.Testing;

public static class LegacyArchiveFilesQualification
{
    public static async Task RunAsync(string fault = "valid", CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("Protected archives require Linux.");
        string root = Path.Combine(Path.GetTempPath(), "teleg-history-archive-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            using var files = new SyntheticSecretFiles(); using var secrets = SecretFiles.Load(files.Root, "service-config.json", files.Grant());
            var context = CipherQualification.Context(SyntheticSecretFiles.BrandA); Guid target = Guid.NewGuid(), member = Guid.NewGuid();
            using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "legacy-fernet-synthetic.json")));
            var vector = vectors.RootElement.GetProperty("vectors").EnumerateArray().First(v => v.GetProperty("kind").GetString() == "Text");
            string[] keys = vector.GetProperty("keys").EnumerateArray().Select(v => v.GetString()!).ToArray();
            var source = new LegacySourceRecord(LegacyProjection.InventoryUnit, "71", "{\"id\":71,\"order_id\":\"PRIVATE-ARCHIVE-ORDER\",\"data\":" + vector.GetProperty("ciphertext").GetRawText() + "}");
            VerifiedImportManifest Plan(LegacySourceRecord row, Guid batch)
            {
                byte[] body = VerifiedImportManifest.EncodePlan(SchemaManifest.ForQualification("Host=127.0.0.1;Database=shop_qualification_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa;Username=shop_test_runner"), "public-a", context.BrandId, "66371a23a1c8149dc5bdf89a0355aa689d454dba", SHA256.HashData("synthetic schema"u8), "protected:synthetic/frozen-copy", SHA256.HashData("synthetic copy"u8), batch, [row.Proof(target, context, secrets)]);
                return VerifiedImportManifest.Open("protected:synthetic/file-archive-plan", SHA256.HashData(body), body);
            }
            Guid batch = Guid.NewGuid(); var manifest = Plan(source, batch); var store = new LegacyArchiveFiles(root, secrets);
            ArchivedLegacyRecord written;
            if (fault == "race")
            {
                var raced = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => new LegacyArchiveFiles(root, secrets).WriteAsync(context, manifest, source, target, keys, ct))); written = raced[0];
                if (raced.Any(r => !r.Digest.AsSpan().SequenceEqual(written.Digest))) throw new InvalidOperationException("Concurrent archive publication changed retained evidence.");
            }
            else written = await store.WriteAsync(context, manifest, source, target, keys, ct);
            string path = Path.Combine(root, target.ToString("N") + ".archive.json"); byte[] original = await File.ReadAllBytesAsync(path, ct);
            if ((File.GetUnixFileMode(path) & (UnixFileMode)63) != 0 || Encoding.UTF8.GetString(original).Contains("PRIVATE-ARCHIVE-ORDER", StringComparison.Ordinal)) throw new InvalidOperationException("Protected archive leaked source text or permissions.");
            async Task Reject(Func<Task> action)
            { try { await action(); throw new InvalidOperationException("Unverified persisted archive accepted."); } catch (MigrationRejectedException) { } }
            if (fault == "missing") { File.Delete(path); await Reject(async () => { await store.ReadAsync(context, manifest, target, keys, ct); }); return; }
            if (fault == "shared") { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead); await Reject(async () => { await store.ReadAsync(context, manifest, target, keys, ct); }); return; }
            if (fault == "root") { File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute); await Reject(async () => { await store.ReadAsync(context, manifest, target, keys, ct); }); return; }
            if (fault == "symlink") { File.Move(path, path + ".original"); File.CreateSymbolicLink(path, path + ".original"); await Reject(async () => { await store.ReadAsync(context, manifest, target, keys, ct); }); return; }
            if (fault == "scope") { await Reject(async () => { await store.ReadAsync(CipherQualification.Context(SyntheticSecretFiles.BrandB), manifest, target, keys, ct); }); return; }
            if (fault == "key") { await Reject(async () => { await store.ReadAsync(context, manifest, target, ["synthetic wrong source key"], ct); }); return; }
            if (fault == "source")
            {
                var changed = source with { Body = source.Body.Replace("PRIVATE-ARCHIVE-ORDER", "CHANGED-ARCHIVE-ORDER", StringComparison.Ordinal) }; var next = Plan(changed, batch);
                await Reject(async () => { await store.WriteAsync(context, next, changed, target, keys, ct); });
                if (!(await File.ReadAllBytesAsync(path, ct)).AsSpan().SequenceEqual(original)) throw new InvalidOperationException("Frozen persisted archive was overwritten."); return;
            }
            if (fault is "cipher" or "field" or "duplicate")
            {
                var json = JsonNode.Parse(original)!;
                if (fault == "cipher") { byte[] bytes = Convert.FromBase64String(json["ciphertext"]!.GetValue<string>()); bytes[^1] ^= 1; json["ciphertext"] = Convert.ToBase64String(bytes); }
                else if (fault == "field") json["fields"]![0]!["field"] = "unknown_field";
                else { await File.WriteAllTextAsync(path, Encoding.UTF8.GetString(original).Replace("\"version\":1,", "\"version\":1,\"version\":1,", StringComparison.Ordinal), ct); await Reject(async () => { await store.ReadAsync(context, manifest, target, keys, ct); }); return; }
                await File.WriteAllTextAsync(path, json.ToJsonString(), ct); await Reject(async () => { await store.ReadAsync(context, manifest, target, keys, ct); }); return;
            }
            var restored = await new LegacyArchiveFiles(root, secrets).ReadAsync(context, manifest, target, keys, ct);
            var targetOnly = await new LegacyArchiveFiles(root, secrets).ReadAsync(context, manifest, target, ct);
            if (!restored.Digest.AsSpan().SequenceEqual(targetOnly.Digest)) throw new InvalidOperationException("Protected target archive still requires a legacy key.");
            var replay = await new LegacyArchiveFiles(root, secrets).WriteAsync(context, manifest, source, target, keys, ct);
            if (restored.Source != source || !written.Digest.AsSpan().SequenceEqual(restored.Digest) || !written.Digest.AsSpan().SequenceEqual(replay.Digest) || !(await File.ReadAllBytesAsync(path, ct)).AsSpan().SequenceEqual(original) || Directory.GetFiles(root).Length != 1) throw new InvalidOperationException("Protected archive reload or replay changed evidence.");
            var order = new LegacySourceRecord(LegacyProjection.OrderRecord, "PRIVATE-ARCHIVE-ORDER", "{\"id\":\"PRIVATE-ARCHIVE-ORDER\",\"user_id\":99001234,\"product_id\":456,\"qty\":2,\"total\":\"600000\",\"currency\":\"IRR\",\"created_at\":\"2024-02-01T00:00:00Z\",\"checkout_snapshot\":{}}");
            byte[] orderBody = VerifiedImportManifest.EncodePlan(manifest.Destination, manifest.SourceBrand, context.BrandId, manifest.SourceRevision, Convert.FromHexString(manifest.SourceSchemaDigest), manifest.SnapshotReference, Convert.FromHexString(manifest.SnapshotDigest), Guid.NewGuid(), [new(LegacyProjection.User, "99001234", member, context.BrandId, SHA256.HashData("synthetic member"u8)), order.Proof(target, context, secrets)]);
            var orderPlan = VerifiedImportManifest.Open("protected:synthetic/read-only-file-plan", SHA256.HashData(orderBody), orderBody); string orderRoot = Path.Combine(root, "teleg-history-archive-orders"); Directory.CreateDirectory(orderRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var orderStore = new LegacyArchiveFiles(orderRoot, secrets); await orderStore.WriteAsync(context, orderPlan, order, target, [], ct);
            var history = await new LegacyArchiveFiles(orderRoot, secrets).ReadAsync(context, orderPlan, target, [], ct); var view = new LegacyReadOnlyOrderHistory(secrets).Read(context, orderPlan, member, history, history.Evidence);
            if (!view.ReadOnly || !view.IncompleteTerms || view.ImportApproved || view.OriginalCurrency != "IRR" || view.OriginalTotal != 600000) throw new InvalidOperationException("Persisted incomplete history lost original facts.");
            if (fault == "zero")
            {
                var zero = new LegacySourceRecord(LegacyProjection.ZeroTransaction, "PRIVATE-ARCHIVE-ORDER", "{\"id\":\"PRIVATE-ARCHIVE-ORDER\",\"user_id\":99001234,\"amount\":0,\"currency\":\"USD\",\"created_at\":\"2024-02-01T00:00:01Z\"}");
                byte[] body = VerifiedImportManifest.EncodePlan(orderPlan.Destination, orderPlan.SourceBrand, context.BrandId, orderPlan.SourceRevision, Convert.FromHexString(orderPlan.SourceSchemaDigest), orderPlan.SnapshotReference, Convert.FromHexString(orderPlan.SnapshotDigest), orderPlan.Batch, [zero.Proof(target, context, secrets)]);
                var zeroPlan = VerifiedImportManifest.Open("protected:synthetic/derived-zero", SHA256.HashData(body), body);
                string orderPath = Path.Combine(orderRoot, target.ToString("N") + ".archive.json"), zeroPath = Path.Combine(orderRoot, target.ToString("N") + ".zero.archive.json");
                byte[] originalOrder = await File.ReadAllBytesAsync(orderPath, ct);
                var firstZero = await orderStore.WriteZeroAsync(context, zeroPlan, zero, target, ct);
                byte[] originalZero = await File.ReadAllBytesAsync(zeroPath, ct);
                var replayZero = await orderStore.WriteZeroAsync(context, zeroPlan, zero, target, ct);
                var restoredZero = await new LegacyArchiveFiles(orderRoot, secrets).ReadZeroAsync(context, zeroPlan, target, ct);
                if (firstZero.Source != zero || !firstZero.Digest.AsSpan().SequenceEqual(replayZero.Digest) || !firstZero.Digest.AsSpan().SequenceEqual(restoredZero.Digest) || !restoredZero.Reference.EndsWith("/native-zero", StringComparison.Ordinal) || !(await File.ReadAllBytesAsync(orderPath, ct)).AsSpan().SequenceEqual(originalOrder) || !(await File.ReadAllBytesAsync(zeroPath, ct)).AsSpan().SequenceEqual(originalZero)) throw new InvalidOperationException("Derived zero archive changed original evidence or replay.");
                await Reject(async () => { await orderStore.ReadZeroAsync(context, orderPlan, target, ct); });
                await Reject(async () => { await orderStore.WriteZeroAsync(context, zeroPlan, zero with { Body = zero.Body.Replace("99001234", "99001235", StringComparison.Ordinal) }, target, ct); });
                await Reject(async () => { await orderStore.WriteZeroAsync(context, zeroPlan, order, target, ct); });
                if (!(await File.ReadAllBytesAsync(orderPath, ct)).AsSpan().SequenceEqual(originalOrder) || !(await File.ReadAllBytesAsync(zeroPath, ct)).AsSpan().SequenceEqual(originalZero)) throw new InvalidOperationException("Rejected derived zero changed retained evidence.");
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
