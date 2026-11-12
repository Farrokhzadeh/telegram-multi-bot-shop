using Npgsql;
using Platform.Migration;
using Platform.Persistence;
using Platform.Testing;
namespace Platform.IntegrationTests;

[Collection("PostgreSQL races")]
public sealed class ReadOnlyReconciliationTargetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly string Backup = new('a', 64);
    private static async Task WithTarget(Func<CheckoutFixture, NpgsqlDataSource, VerifiedImportManifest, LegacyWalletSnapshot, Task> check)
    {
        await using var f = await CheckoutFixture.CreateAsync(users: 0, stock: 0, ct: Ct); await MigrationQualification.EnableRoleAsync(f, Ct);
        var row = MigrationQualification.Wallet(); var manifest = MigrationQualification.Manifest(f, "public-readonly", f.Brands[0], [row]);
        await new OpeningBalanceImportWorkflow(f.Sessions).ImportAsync(f.Context(0), manifest, row, Ct);
        var options = new NpgsqlConnectionStringBuilder(f.Database.Data.ConnectionString); SchemaManifest.ForQualification(options.ConnectionString).Validate(); string database = options.Database!;
        await using (var setup = f.Database.Data.CreateCommand("DO $$ BEGIN CREATE ROLE teleg_target_reader LOGIN PASSWORD 'synthetic-target-reader-only' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS; EXCEPTION WHEN duplicate_object THEN NULL; END $$; REVOKE CREATE,TEMPORARY ON DATABASE \"" + database + "\" FROM PUBLIC; GRANT CONNECT ON DATABASE \"" + database + "\" TO teleg_target_reader; REVOKE CREATE ON SCHEMA public FROM PUBLIC; GRANT USAGE ON SCHEMA shop TO teleg_target_reader; GRANT SELECT ON ALL TABLES IN SCHEMA shop TO teleg_target_reader; COMMENT ON DATABASE \"" + database + "\" IS 'teleg-t066-reconciliation-copy:" + Backup + "'")) await setup.ExecuteNonQueryAsync(Ct);
        options.Username = "teleg_target_reader"; options.Password = "synthetic-target-reader-only"; options.Enlist = false; options.IncludeErrorDetail = false;
        await using var reader = new NpgsqlSlimDataSourceBuilder(options.ConnectionString).Build(); await check(f, reader, manifest, row);
    }
    [Fact]
    public async Task ReaderComparesWalletAndRecordReportsWithoutImportAuthority() => await WithTarget(async (f, reader, manifest, row) =>
    {
        var sessions = new DbSessionFactory(reader); await Assert.ThrowsAsync<MigrationRejectedException>(() => new ReconciliationReport(sessions).ReadAsync(manifest, Ct));
        var grant = await ReadOnlyReconciliationTarget.VerifyAsync(reader, manifest, Backup, true, Ct); var report = new ReconciliationReport(sessions, grant);
        Assert.True((await report.ReadAsync(manifest, Ct)).Complete); Assert.True((await report.CompareWalletsAsync(manifest, [row], Ct)).Matched);
        var records = await new LegacyRecordReconciliationReport(reader, f.Secrets, grant).CompareAsync(f.Context(0), manifest, new([row], [], [], [], [], []), (_, _) => throw new MigrationRejectedException(), Ct); Assert.True(records.Matched); Assert.False(records.ReleaseReady);
        await Assert.ThrowsAsync<MigrationRejectedException>(() => new OpeningBalanceImportWorkflow(sessions).ImportAsync(f.Context(0), manifest, row, Ct));
        await using var write = reader.CreateCommand("DELETE FROM shop.wallets WHERE false"); var error = await Assert.ThrowsAsync<PostgresException>(() => write.ExecuteNonQueryAsync(Ct)); Assert.Equal("42501", error.SqlState);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallets", Ct)); Assert.DoesNotContain(manifest.Reference, grant.ToString());
    });
    [Fact]
    public async Task ExplicitGrantBackupAndRestrictedIdentityAreRequired() => await WithTarget(async (f, reader, manifest, _) =>
    {
        await Assert.ThrowsAsync<MigrationRejectedException>(() => ReadOnlyReconciliationTarget.VerifyAsync(reader, manifest, Backup, false, Ct));
        await Assert.ThrowsAsync<MigrationRejectedException>(() => ReadOnlyReconciliationTarget.VerifyAsync(reader, manifest, "invalid", true, Ct));
        await Assert.ThrowsAsync<MigrationRejectedException>(() => ReadOnlyReconciliationTarget.VerifyAsync(reader, manifest, new string('b', 64), true, Ct));
        await Assert.ThrowsAsync<MigrationRejectedException>(() => ReadOnlyReconciliationTarget.VerifyAsync(f.Database.Data, manifest, Backup, true, Ct));
    });
    [Fact]
    public async Task GrantCannotBeBorrowedByAnotherManifestOrReference() => await WithTarget(async (f, reader, manifest, row) =>
    {
        var grant = await ReadOnlyReconciliationTarget.VerifyAsync(reader, manifest, Backup, true, Ct); var report = new ReconciliationReport(new(reader), grant);
        var other = MigrationQualification.Manifest(f, "public-other", f.Brands[0], [row]); await Assert.ThrowsAsync<MigrationRejectedException>(() => report.ReadAsync(other, Ct));
        var changedReference = VerifiedImportManifest.Open("protected:synthetic/other-reference", manifest.Digest.Span, manifest.Encode()); await Assert.ThrowsAsync<MigrationRejectedException>(() => report.ReadAsync(changedReference, Ct));
    });
    [Fact]
    public async Task ElevatedReaderPrivilegesRejectNewAndExistingGrants() => await WithTarget(async (f, reader, manifest, _) =>
    {
        var grant = await ReadOnlyReconciliationTarget.VerifyAsync(reader, manifest, Backup, true, Ct);
        await using var alter = f.Database.Data.CreateCommand("GRANT UPDATE ON shop.wallets TO teleg_target_reader"); await alter.ExecuteNonQueryAsync(Ct);
        await Assert.ThrowsAsync<MigrationRejectedException>(() => ReadOnlyReconciliationTarget.VerifyAsync(reader, manifest, Backup, true, Ct));
        await Assert.ThrowsAsync<MigrationRejectedException>(() => new ReconciliationReport(new(reader), grant).ReadAsync(manifest, Ct));
    });
    [Fact]
    public async Task OwnershipOutsideTablesRejectsReaderGrant() => await WithTarget(async (f, reader, manifest, _) =>
    {
        var grant = await ReadOnlyReconciliationTarget.VerifyAsync(reader, manifest, Backup, true, Ct);
        await using var alter = f.Database.Data.CreateCommand("CREATE DOMAIN public.synthetic_owned_domain AS integer; ALTER DOMAIN public.synthetic_owned_domain OWNER TO teleg_target_reader"); await alter.ExecuteNonQueryAsync(Ct);
        await Assert.ThrowsAsync<MigrationRejectedException>(() => ReadOnlyReconciliationTarget.VerifyAsync(reader, manifest, Backup, true, Ct));
        await Assert.ThrowsAsync<MigrationRejectedException>(() => new ReconciliationReport(new(reader), grant).ReadAsync(manifest, Ct));
    });
    [Fact]
    public async Task CatalogDriftInvalidatesExistingGrant() => await WithTarget(async (f, reader, manifest, _) =>
    {
        var grant = await ReadOnlyReconciliationTarget.VerifyAsync(reader, manifest, Backup, true, Ct);
        await using var alter = f.Database.Data.CreateCommand("CREATE INDEX synthetic_reconciliation_drift ON shop.users(language)"); await alter.ExecuteNonQueryAsync(Ct);
        await Assert.ThrowsAsync<MigrationRejectedException>(() => new ReconciliationReport(new(reader), grant).ReadAsync(manifest, Ct));
    });
    [Fact]
    public async Task ChangedCopyMarkerInvalidatesExistingGrant() => await WithTarget(async (f, reader, manifest, _) =>
    {
        var grant = await ReadOnlyReconciliationTarget.VerifyAsync(reader, manifest, Backup, true, Ct); string database = new NpgsqlConnectionStringBuilder(reader.ConnectionString).Database!;
        await using var alter = f.Database.Data.CreateCommand("COMMENT ON DATABASE \"" + database + "\" IS 'synthetic-wrong-copy'"); await alter.ExecuteNonQueryAsync(Ct);
        await Assert.ThrowsAsync<MigrationRejectedException>(() => new ReconciliationReport(new(reader), grant).ReadAsync(manifest, Ct));
    });
}
