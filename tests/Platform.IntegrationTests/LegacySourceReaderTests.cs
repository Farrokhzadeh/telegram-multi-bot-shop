using System.Security.Cryptography;
using Npgsql;
using Platform.Migration;
using Platform.Persistence;
using Platform.Testing;
namespace Platform.IntegrationTests;
[Collection("PostgreSQL races")]
public sealed class LegacySourceReaderTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("balance")]
    [InlineData("irr")]
    [InlineData("schema")]
    [InlineData("migration")]
    [InlineData("endpoint")]
    [InlineData("constraint")]
    [InlineData("index")]
    public async Task ApprovedCopyImportIsRepeatSafeAndRejectsChangedSourceBeforeTargetWrites(string fault)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await CheckoutFixture.CreateAsync(users: 0, stock: 0, ct: ct);
        await MigrationQualification.EnableRoleAsync(f, ct);
        await using (var setup = f.Database.Data.CreateCommand("""
            CREATE TABLE public.users(id bigint PRIMARY KEY,balance numeric(18,2),balance_irr numeric(24,2),lang text,joined_at timestamptz,last_seen_at timestamptz,is_active boolean,is_banned boolean);
            CREATE TABLE public.transactions(id text PRIMARY KEY,user_id bigint,amount numeric(24,2),currency text,created_at timestamptz);
            CREATE TABLE public.django_migrations(id bigint PRIMARY KEY,app text,name text);
            INSERT INTO public.django_migrations VALUES(1,'accounts','0001_initial');
            INSERT INTO public.users VALUES(99001234,12.34,0,'en','2024-01-01 00:00:00+00','2024-01-02 00:00:00+00',true,false);
            INSERT INTO public.transactions VALUES('source-entry-1',99001234,100,'USD','2024-01-01 01:00Z'),('source-entry-2',99001234,-10,'USD','2024-01-01 02:00Z');
            """)) await setup.ExecuteNonQueryAsync(ct);
        var endpoint = new NpgsqlConnectionStringBuilder(f.Database.Data.ConnectionString);
        var reader = new LegacySourceReader(f.Database.Data, new(endpoint.Host!, endpoint.Port, endpoint.Database!, endpoint.Username!));
        byte[] schema = await reader.InspectSchemaAsync(ct);
        var row = MigrationQualification.Wallet() with { LegacyUser = "99001234", History = MigrationQualification.Wallet().History.Select(h => h with { BalanceAfter = null }).ToArray() };
        byte[] body = VerifiedImportManifest.EncodePlan(SchemaManifest.ForQualification(f.Database.Data.ConnectionString), "public-a", f.Brands[0], "66371a23a1c8149dc5bdf89a0355aa689d454dba", schema, "protected:synthetic/source", SHA256.HashData("synthetic copy"u8), Guid.NewGuid(), row.Proofs(f.Brands[0]), row.BalanceUsd);
        var manifest = VerifiedImportManifest.Open("protected:synthetic/manifest", SHA256.HashData(body), body);
        string? mutation = fault switch
        {
            "constraint" => "ALTER TABLE public.users ADD CONSTRAINT positive_balance CHECK(balance>=0)",
            "index" => "CREATE INDEX legacy_language_probe ON public.users(lang)",
            "balance" => "UPDATE public.users SET balance=13",
            "irr" => "UPDATE public.users SET balance_irr=1",
            "schema" => "ALTER TABLE public.users ADD COLUMN unexpected text",
            "migration" => "INSERT INTO public.django_migrations VALUES(2,'accounts','unexpected')",
            _ => null
        };
        if (mutation is not null) { await using var change = f.Database.Data.CreateCommand(mutation); await change.ExecuteNonQueryAsync(ct); }
        if (fault == "endpoint") reader = new(f.Database.Data, new(endpoint.Host!, endpoint.Port, endpoint.Database! + "_other", endpoint.Username!));
        var workflow = new ImportWorkflow(reader, f.Sessions);
        var targets = row.History.ToDictionary(h => h.LegacyId, h => h.TargetEntry);
        if (fault == "valid")
        {
            var report = await workflow.ImportWalletAsync(f.Context(0), manifest, row.TelegramId, row.TargetUser, row.TargetOpening, targets, ct);
            Assert.True(report.Complete); Assert.False(report.ReleaseReady); Assert.Equal(12.34m, report.OpeningUsd);
            var expected = new LegacyAggregateExpectations(manifest.Batch, Convert.ToHexStringLower(manifest.Digest.Span), 0, 0, 0);
            var comparison = await new ReconciliationReport(f.Sessions).CompareAsync(manifest, expected, ct);
            Assert.True(comparison.Matched); Assert.False(comparison.ReleaseReady);
            var mismatch = LegacyAggregateComparison.Compare(manifest, expected with { PaidUnfulfilledOrders = 1, HeldCouponUnits = 2, TargetCiphertexts = 3 }, report);
            Assert.False(mismatch.Matched); Assert.Equal(-1L, mismatch.PaidObligationDifference); Assert.Equal(-2L, mismatch.HeldCouponDifference); Assert.Equal(-3L, mismatch.CiphertextDifference);
            Assert.Throws<MigrationRejectedException>(() => LegacyAggregateComparison.Compare(manifest, expected with { Batch = Guid.NewGuid() }, report));
            Assert.Throws<MigrationRejectedException>(() => LegacyAggregateComparison.Compare(manifest, expected with { ManifestDigest = new string('0', 64) }, report));
            Assert.Throws<MigrationRejectedException>(() => LegacyAggregateComparison.Compare(manifest, expected with { TargetCiphertexts = -1 }, report));
            Assert.False(LegacyAggregateComparison.Compare(manifest, expected, report with { SaleableInventory = 1 }).Matched);
            Assert.False(LegacyAggregateComparison.Compare(manifest, expected, report with { Complete = false }).Matched);
            Assert.True((await workflow.ImportWalletAsync(f.Context(0), manifest, row.TelegramId, row.TargetUser, row.TargetOpening, targets, ct)).Complete);
            Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE is_posting", ct));
        }
        else
        {
            await Assert.ThrowsAsync<MigrationRejectedException>(() => workflow.ImportWalletAsync(f.Context(0), manifest, row.TelegramId, row.TargetUser, row.TargetOpening, targets, ct));
            Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.users", ct));
            Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.legacy_id_map", ct));
        }
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM public.transactions", ct));
    }
}
