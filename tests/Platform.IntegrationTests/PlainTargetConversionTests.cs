using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using Platform.Persistence.Migrations;
using Platform.Migration;
using Platform.Migration.Recovery;
using Platform.Persistence;
using Platform.Persistence.Entities;
using Platform.Security;
using Platform.Testing;
using Platform.SchemaRunner;

namespace Platform.IntegrationTests;

public sealed class PlainTargetConversionTests
{
    [Fact]
    public async Task PreviousKeyRecoversExactBytesAndWrongKeyLeavesDestinationUntracked()
    {
        var ct = TestContext.Current.CancellationToken;
        using var files = new SyntheticSecretFiles();
        var registry = new BotBrandRegistry([new("conversion-test", "original", SyntheticSecretFiles.BrandA, 101, SyntheticSecretFiles.Deployment, 7, true)]);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "original")], "conversion-test"));
        Assert.True(registry.TryResolveAuthenticated("conversion-test", principal, out var context));

        files.WriteManifest(activeKey: "old");
        using var writerKeys = RecoverySecretFiles.Load(files.Root, "service-config.json", files.Grant());
        files.WriteManifest();
        using var recoveryKeys = RecoverySecretFiles.Load(files.Root, "service-config.json", files.Grant());
        using var other = new SyntheticSecretFiles();
        using var wrongKeys = RecoverySecretFiles.Load(other.Root, "service-config.json", other.Grant());
        Assert.Equal("current", recoveryKeys.CurrentBrandKeyId(context!));

        byte[] content = [0, 128, 255, .. "اصل"u8];
        byte[] cipher = new BrandCipher(writerKeys).Encrypt(context!, "brands", context!.BrandId, "bot_token_ciphertext", content);
        var original = new Brand { Id = context.BrandId, BotId = context.BotId, BotTokenCiphertext = cipher, BotTokenKeyId = "old" };
        await using var source = new SqliteRecoveryTargetDbContext(new DbContextOptionsBuilder<SqliteRecoveryTargetDbContext>().UseSqlite("Data Source=:memory:").Options);
        await using var target = new PlainTargetCopyDbContext(new DbContextOptionsBuilder<PlainTargetCopyDbContext>().UseSqlite("Data Source=:memory:").Options);

        var rejected = new Brand();
        await Assert.ThrowsAsync<MigrationRejectedException>(() => new TargetPlaintextRows(wrongKeys, _ => context!).CopyAsync(source, target, original, rejected, ct));
        Assert.Empty(target.ChangeTracker.Entries());

        var copied = new Brand();
        await new TargetPlaintextRows(recoveryKeys, _ => context!).CopyAsync(source, target, original, copied, ct);
        Assert.Equal(content, copied.BotToken);
        Assert.Equal(original.Id, copied.Id);
        Assert.Equal(original.BotId, copied.BotId);
        Assert.Null(copied.BankNumber);
        Assert.Null(copied.BankHolder);
        Assert.Equal(EntityState.Added, target.Entry(copied).State);
        Assert.Same(cipher, original.BotTokenCiphertext);
        Assert.Equal("old", original.BotTokenKeyId);
    }
    [Fact]
    public async Task CommittedWalRowsCopyExactlyAndChangedDestinationFailsVerification()
    {
        var ct = TestContext.Current.CancellationToken;
        using var keys = new SyntheticSecretFiles();
        using var recovery = RecoverySecretFiles.Load(keys.Root, "service-config.json", keys.Grant());
        var folder = Directory.CreateTempSubdirectory("akanti-plain-copy-");
        try
        {
            string oldPath = Path.Combine(folder.FullName, "encrypted.db"), newPath = Path.Combine(folder.FullName, "plaintext.db");
            await SqliteSchema.InitializeAsync(oldPath, ct);
            await using var writer = await OpenAsync(oldPath, SqliteOpenMode.ReadWrite, ct);
            await using var destination = await OpenAsync(newPath, SqliteOpenMode.ReadWriteCreate, ct);
            await using var schema = new PlainTargetDbContext(new DbContextOptionsBuilder<PlainTargetDbContext>().UseSqlite(destination).Options);
            await PlainTargetSchema.CreateAsync(schema, ct, forConversion: true);
            await destination.CloseAsync();
            destination.ConnectionString = new SqliteConnectionStringBuilder { DataSource = newPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false, ForeignKeys = true }.ToString();
            await destination.OpenAsync(ct);
            SqliteSchema.Configure(destination);

            Guid product = Guid.NewGuid(), item = Guid.NewGuid();
            byte[] original = [0, 128, 255, .. "اعتبار"u8];
            await using (var old = new SqliteRecoveryTargetDbContext(new DbContextOptionsBuilder<SqliteRecoveryTargetDbContext>().UseSqlite(writer).Options))
            {
                old.Add(new Product { Id = product, FulfillmentKind = "local", AvailabilityPolicy = "immediate", Title = "Snapshot fixture" });
                old.Add(new InventoryItem { Id = item, ProductId = product, CredentialCiphertext = new InventoryCipher(recovery).Encrypt(item, original),
                    CredentialKeyId = recovery.CurrentInventoryKeyId, SourceFingerprint = SHA256.HashData(original), OriginKind = "local_import", State = "available" });
                await old.SaveChangesAsync(ct);
            }
            Assert.True(File.Exists(oldPath + "-wal"));
            await using var source = await OpenAsync(oldPath, SqliteOpenMode.ReadOnly, ct);
            byte[] snapshot = await SqlitePlaintextRowCopy.InspectAsync(source, ct);
            var copy = new SqlitePlaintextRowCopy(recovery, _ => throw new MigrationRejectedException());
            using var otherKeys = new SyntheticSecretFiles();
            using var wrongRecovery = RecoverySecretFiles.Load(otherKeys.Root, "service-config.json", otherKeys.Grant());
            var failing = new SqlitePlaintextRowCopy(wrongRecovery, _ => throw new MigrationRejectedException());
            await Assert.ThrowsAsync<MigrationRejectedException>(() => failing.TransferAsync(source, destination, snapshot, false, ct));
            await using (var pending = await OpenAsync(newPath, SqliteOpenMode.ReadOnly, ct))
                await Assert.ThrowsAsync<MigrationRejectedException>(() => copy.TransferAsync(source, pending, snapshot, true, ct));
            byte[] binding = SHA256.HashData("row interruption fixture"u8);
            var interrupted = new SqlitePlaintextRowCopy(recovery, _ => throw new MigrationRejectedException(),
                () => throw new IOException("Synthetic row interruption."));
            await Assert.ThrowsAsync<IOException>(() => interrupted.TransferAsync(source, destination, snapshot, false, ct, binding));
            await using (var rows = destination.CreateCommand())
            {
                rows.CommandText = "SELECT count(*) FROM inventory_items";
                Assert.Equal(1L, await rows.ExecuteScalarAsync(ct));
            }
            await Assert.ThrowsAsync<MigrationRejectedException>(() => copy.TransferAsync(source, destination, snapshot, false, ct, new byte[32]));
            Assert.Equal(2L, await copy.TransferAsync(source, destination, snapshot, false, ct, binding));
            Assert.Equal(2L, await copy.TransferAsync(source, destination, snapshot, false, ct, binding));
            byte[] foreign = snapshot.ToArray(); foreign[0] ^= 1;
            await Assert.ThrowsAsync<MigrationRejectedException>(() => copy.TransferAsync(source, destination, foreign, false, ct));
            await using (var reader = await OpenAsync(newPath, SqliteOpenMode.ReadOnly, ct))
                Assert.Equal(2L, await copy.TransferAsync(source, reader, snapshot, true, ct, binding));

            await using (var edit = destination.CreateCommand())
            {
                edit.CommandText = "UPDATE inventory_items SET credential_content=x'0102'";
                Assert.Equal(1, await edit.ExecuteNonQueryAsync(ct));
            }
            await using var changed = await OpenAsync(newPath, SqliteOpenMode.ReadOnly, ct);
            await Assert.ThrowsAsync<InvalidOperationException>(() => copy.TransferAsync(source, changed, snapshot, true, ct, binding));
        }
        finally { folder.Delete(recursive: true); }
    }

    [Fact]
    public async Task AssetInterruptionResumesFromVerifiedRowsAndKeepsCompletedAsset()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new SyntheticSecretFiles();
        using var keys = RecoverySecretFiles.Load(fixture.Root, "service-config.json", fixture.Grant());
        var folder = Directory.CreateTempSubdirectory("akanti-plain-assets-");
        try
        {
            string sourcePath = Path.Combine(folder.FullName, "source.db"), targetPath = Path.Combine(folder.FullName, "destination.db");
            await SqliteSchema.InitializeAsync(sourcePath, ct);
            await using (var writer = await OpenAsync(sourcePath, SqliteOpenMode.ReadWrite, ct))
            await using (var data = new SqliteRecoveryTargetDbContext(new DbContextOptionsBuilder<SqliteRecoveryTargetDbContext>().UseSqlite(writer).Options))
            {
                foreach (int number in new[] { 1, 2 })
                    data.Add(new Product { Id = Guid.NewGuid(), Title = "Synthetic asset", FulfillmentKind = "local", AvailabilityPolicy = "immediate", IconRef = $"protected:fixture/{number}" });
                await data.SaveChangesAsync(ct);
            }
            await using (var create = await OpenAsync(targetPath, SqliteOpenMode.ReadWriteCreate, ct))
            await using (var data = new PlainTargetDbContext(new DbContextOptionsBuilder<PlainTargetDbContext>().UseSqlite(create).Options))
                await PlainTargetSchema.CreateAsync(data, ct, forConversion: true);
            await using var destination = await OpenAsync(targetPath, SqliteOpenMode.ReadWrite, ct);
            byte[] schema;
            await using (var transaction = destination.BeginTransaction(deferred: true))
                schema = await PlainTargetSchema.SqliteSchemaDigestAsync(destination, transaction, ct);
            await using var source = await OpenAsync(sourcePath, SqliteOpenMode.ReadOnly, ct);
            var references = new List<TargetAssetReference>();
            byte[] snapshot = await SqlitePlaintextRowCopy.InspectAsync(source, ct, references.Add);
            Assert.Equal(2, references.Count);
            byte[] original = [0, 255, 2, .. "آکانتی"u8];
            string digest = Convert.ToHexStringLower(SHA256.HashData(original));
            var manifest = new TargetConversionManifest(Convert.ToHexStringLower(snapshot), references.Select(reference =>
                new TargetConversionAsset(TargetConversionManifest.ReferenceCommitment(reference), digest, digest)));
            var output = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var conversion = new TargetPlaintextConversion(keys, _ => throw new MigrationRejectedException());
            Task<TargetConversionReceipt> Run(SqliteConnection db, string identity, bool verify, bool interrupt = false) =>
                conversion.RunAsync(source, db, manifest, schema, identity, (_, _) => Task.FromResult(original.ToArray()),
                    (asset, content) => TargetPlaintextAssets.Plain(asset, content.Span),
                    (asset, _) => Task.FromResult(output.TryGetValue(asset.ReferenceCommitment, out var content) ? content.ToArray() : null),
                    verify ? null : (asset, content, _) =>
                    {
                        if (interrupt && output.Count == 1) throw new IOException("Synthetic interruption.");
                        output.Add(asset.ReferenceCommitment, content.ToArray());
                        return Task.CompletedTask;
                    }, () => Assert.Equal(2, output.Count), ct);
            await Assert.ThrowsAsync<IOException>(() => Run(destination, "private-fixture", false, interrupt: true));
            Assert.Single(output);
            await using var verification = await OpenAsync(targetPath, SqliteOpenMode.ReadOnly, ct);
            await Assert.ThrowsAsync<MigrationRejectedException>(() => Run(verification, "private-fixture", true));
            await Assert.ThrowsAsync<MigrationRejectedException>(() => Run(destination, "foreign-fixture", false));
            var receipt = await Run(destination, "private-fixture", false);
            Assert.Equal(2L, receipt.Rows);
            Assert.Equal(2, receipt.Assets);
            Assert.Equal(receipt, await Run(verification, "private-fixture", true));
            foreach (byte[] content in output.Values) Assert.Equal(original, content);
        }
        finally { folder.Delete(recursive: true); }
    }

    [Fact]
    public async Task VerifiedRuntimeCopyRetainsStagedOriginalAndRejectsAssetTampering()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("akanti-ef-qualification-");
        try
        {
            string Child(string name) => Path.Combine(directory.FullName, name);
            Guid brandId = SyntheticSecretFiles.BrandA;
            Guid deployment = SyntheticSecretFiles.Deployment;
            const long botId = 1001;
            using var files = new SyntheticSecretFiles(directory.FullName);
            files.WriteManifest(brands: [brandId]);
            using var keys = RecoverySecretFiles.Load(files.Root, "service-config.json", files.Grant(brands: [brandId]));
            var registry = new BotBrandRegistry([new("conversion-test", "original", brandId, botId, deployment, 7, true)]);
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "original")], "conversion-test"));
            Assert.True(registry.TryResolveAuthenticated("conversion-test", principal, out var scope));
            string original = Child("original.db"), snapshot = Child("source.db"), target = Child("target.db");
            await SqliteSchema.InitializeAsync(original, ct);
            string sourceHash;
            Guid product = Guid.NewGuid();
            await using (var writer = await OpenAsync(original, SqliteOpenMode.ReadWrite, ct))
            await using (var old = new SqliteRecoveryTargetDbContext(new DbContextOptionsBuilder<SqliteRecoveryTargetDbContext>().UseSqlite(writer).Options))
            {
                old.Add(new Brand { Id = brandId, Code = "fixture", InternalName = "Fixture", PublicName = "Fixture",
                    Market = "international", DefaultLanguage = "en", BotUsername = "fixture_bot", BotId = botId,
                    BotTokenCiphertext = new BrandCipher(keys).Encrypt(scope!, "brands", brandId, "bot_token_ciphertext", "synthetic-bot"u8),
                    BotTokenKeyId = keys.CurrentBrandKeyId(scope!) });
                old.Add(new Product { Id = product, Title = "Synthetic", FulfillmentKind = "local", AvailabilityPolicy = "immediate", IconRef = "protected:fixture/icon" });
                await old.SaveChangesAsync(ct);
                sourceHash = await EfSchemaCommand.FreezePlainSqliteSourceAsync(original, snapshot, ct);
            }
            var references = new List<TargetAssetReference>();
            await using (var source = await OpenAsync(snapshot, SqliteOpenMode.ReadOnly, ct))
                await SqlitePlaintextRowCopy.InspectAsync(source, ct, references.Add);
            string reference = TargetConversionManifest.ReferenceCommitment(Assert.Single(references));
            byte[] asset = [0, 255, 42];
            string hash = Convert.ToHexStringLower(SHA256.HashData(asset));
            Directory.CreateDirectory(Child("input-assets"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.CreateDirectory(Child("output-assets"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            string assetFile = reference + ".asset";
            await File.WriteAllBytesAsync(Child("input-assets/" + assetFile), asset, ct);
            File.SetUnixFileMode(Child("input-assets/" + assetFile), UnixFileMode.UserRead);
            string schema = await EfSchemaCommand.PreparePlainSqliteConversionAsync(target, ct);
            string job = Child("job.json");
            string json = $$"""
                {"version":1,"source":"source.db","destination":"target.db","source_assets":"input-assets","destination_assets":"output-assets",
                 "snapshot_sha256":"{{sourceHash}}","target_schema_sha256":"{{schema}}",
                 "keys":{"directory":"{{Path.GetFileName(files.Root)}}","manifest":"service-config.json","deployment":"{{deployment:D}}","revision":7,"preview":null},
                 "brands":[{"id":"{{brandId:D}}","bot_id":{{botId}}}],
                 "assets":[{"format":"plain","reference_sha256":"{{reference}}","stored_sha256":"{{hash}}","plaintext_sha256":"{{hash}}","brand_id":null,"row_id":null,"key_id":null,"update_id":null}]}
                """;
            await File.WriteAllTextAsync(job, json, ct);
            File.SetUnixFileMode(job, UnixFileMode.UserRead);
            string output = Child("runtime-copy");
            await Assert.ThrowsAsync<MigrationRejectedException>(() => EfSchemaCommand.RunPlainSqliteConversionAsync(job, verify: true, ct, output));
            Assert.False(Directory.Exists(output));
            await EfSchemaCommand.RunPlainSqliteConversionAsync(job, verify: false, ct);
            var bundle = await EfSchemaCommand.RunPlainSqliteConversionAsync(job, verify: true, ct, runtimeCopy: output);
            Assert.Equal(1, await EfSchemaCommand.VerifyPlainRuntimeCopyAsync(output, bundle.RuntimeReceiptSha256!, ct));
            await EfSchemaCommand.RunPlainSqliteConversionAsync(job, verify: true, ct);
            var runtime = SqliteSessionFactory.ForPlaintext(Path.Combine(output, "shop.db"));
            Assert.True(await runtime.RunEfAsync(false, null, (data, token) =>
                data.Data.Set<Product>().AnyAsync(row => row.Id == product && row.IconRef == "protected:fixture/icon", token), ct));
            await File.WriteAllBytesAsync(Path.Combine(output, "assets", assetFile), [42, 255, 0], ct);
            await Assert.ThrowsAsync<MigrationRejectedException>(() => EfSchemaCommand.VerifyPlainRuntimeCopyAsync(output, bundle.RuntimeReceiptSha256!, ct));
        }
        finally { directory.Delete(recursive: true); }
    }

    private static async Task<SqliteConnection> OpenAsync(string path, SqliteOpenMode mode, CancellationToken ct)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false, ForeignKeys = true }.ToString());
        try
        {
            await connection.OpenAsync(ct);
            SqliteSchema.Configure(connection);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

}
