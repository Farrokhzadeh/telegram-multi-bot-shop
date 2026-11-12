using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Platform.Migration;
using Platform.Persistence;
namespace Platform.Testing;

public sealed class LegacyWalletRetentionFixture : IAsyncDisposable
{
    public const string Archive = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private LegacyWalletRetentionFixture(CheckoutFixture fixture, LegacySourceReader source, LegacyWalletSnapshot wallet, VerifiedImportManifest manifest, byte[] register)
    { Fixture = fixture; Source = source; Wallet = wallet; Manifest = manifest; Register = register; }
    public CheckoutFixture Fixture { get; }
    public LegacySourceReader Source { get; }
    public LegacyWalletSnapshot Wallet { get; }
    public VerifiedImportManifest Manifest { get; }
    public byte[] Register { get; }
    public Task<ReadOnlyMemory<byte>> Read(string reference, CancellationToken ct) => reference == "protected:synthetic/manual-wallet-history" ? Task.FromResult<ReadOnlyMemory<byte>>(Register) : throw new KeyNotFoundException();
    public static byte[] RegisterBody() => Encoding.UTF8.GetBytes($$"""
        {"version":2,"policy":"manual_irr_and_original_nonposting_history_v2","sources":[{"source":"prod1","brand":"acc-abad","archive_sha256":"{{Archive}}","source_users":1,"wallets":[{"legacy_user_id":"99001234","original_irr_amount":"1234.50"}],"history":[{"id":"usd-original","user_id":"99001234","amount":"7.50","currency":"USD","created_at":"2024-01-01 01:00:00+00","desc":"PRIVATE original history"},{"id":"irr-original","user_id":"99001234","amount":"-900.25","currency":"IRR","created_at":"2024-01-01 02:00:00+00"},{"id":"zero-original","user_id":"99001234","amount":"0","currency":"USD","created_at":"2024-01-01 03:00:00+00"}]},{"source":"prod2","brand":"kuala"},{"source":"prod3","brand":"zikotech"}]}
        """);
    public static async Task<LegacyWalletRetentionFixture> CreateAsync(CancellationToken ct, bool seedCatalog = true)
    {
        var f = await CheckoutFixture.CreateAsync(users: 0, stock: 0, ct: ct, seedCatalog: seedCatalog);
        try
        {
            await MigrationQualification.EnableRoleAsync(f, ct);
            await using (var setup = f.Database.Data.CreateCommand("""
                CREATE TABLE public.django_migrations(id bigint PRIMARY KEY,app text,name text); INSERT INTO public.django_migrations VALUES(1,'accounts','0001_initial');
                CREATE TABLE public.users(id bigint PRIMARY KEY,balance numeric,balance_irr numeric,lang text,joined_at timestamptz,last_seen_at timestamptz,is_active boolean,is_banned boolean);
                INSERT INTO public.users VALUES(99001234,12.34,1234.50,'en','2024-01-01 00:00Z','2024-01-02 00:00Z',true,false);
                CREATE TABLE public.transactions(id text PRIMARY KEY,user_id bigint,amount numeric,currency text,created_at timestamptz);
                INSERT INTO public.transactions VALUES('usd-original',99001234,7.50,'USD','2024-01-01 01:00Z'),('irr-original',99001234,-900.25,'IRR','2024-01-01 02:00Z'),('zero-original',99001234,0,'USD','2024-01-01 03:00Z');
                """)) await setup.ExecuteNonQueryAsync(ct);
            var endpoint = new NpgsqlConnectionStringBuilder(f.Database.Data.ConnectionString);
            var source = new LegacySourceReader(f.Database.Data, new(endpoint.Host!, endpoint.Port, endpoint.Database!, endpoint.Username!));
            byte[] body = RegisterBody(); var retention = LegacyWalletRetention.Open("protected:synthetic/manual-wallet-history", SHA256.HashData(body), body, "acc-abad", Archive, "99001234");
            var wallet = MigrationQualification.Wallet() with { LegacyUser = "99001234", OtherBalance = 1234.50m, History = [new("usd-original", Guid.NewGuid(), 7.50m, "USD", new DateTime(2024, 1, 1, 1, 0, 0, DateTimeKind.Utc), null)], Retention = retention };
            byte[] plan = VerifiedImportManifest.EncodePlan(SchemaManifest.ForQualification(f.Database.Data.ConnectionString), "acc-abad", f.Brands[0], "66371a23a1c8149dc5bdf89a0355aa689d454dba", await source.InspectSchemaAsync(ct), "protected:synthetic/retained-source", Convert.FromHexString(Archive), Guid.NewGuid(), wallet.Proofs(f.Brands[0]), wallet.BalanceUsd);
            return new(f, source, wallet, VerifiedImportManifest.Open("protected:synthetic/retained-wallet-plan", SHA256.HashData(plan), plan), body);
        }
        catch { await f.DisposeAsync(); throw; }
    }
    public ValueTask DisposeAsync() => Fixture.DisposeAsync();
}
public static class LegacyWalletRetentionQualification
{
    public static async Task RunAsync(CancellationToken ct = default)
    {
        await using var world = await LegacyWalletRetentionFixture.CreateAsync(ct); var f = world.Fixture; var row = world.Wallet;
        var workflow = new ImportWorkflow(world.Source, f.Sessions);
        for (int pass = 0; pass < 2; pass++)
        {
            var result = await workflow.ImportWalletAsync(f.Context(0), world.Manifest, row.TelegramId, row.TargetUser, row.TargetOpening, row.History.ToDictionary(h => h.LegacyId, h => h.TargetEntry), ct, retention: row.Retention, readRetention: world.Read);
            if (!result.Complete) throw new InvalidOperationException("Native retained wallet projection failed.");
        }
        var reports = new ReconciliationReport(f.Sessions);
        var report = await reports.CompareWalletsAsync(world.Manifest, [row], ct, world.Read);
        if (!report.Matched || report.ReleaseReady || report.ManualIrrAccounts != 1 || report.RetainedHistoryRows != 3 || report.RetainedIrrHistory != 1 || report.RetainedZeroHistory != 1 ||
            await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", ct) != 2 || await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE is_posting", ct) != 1 ||
            (await reports.CompareWalletsAsync(world.Manifest, [row], ct)).Matched) throw new InvalidOperationException("Native retained wallet reconciliation failed.");
        byte[] changed = world.Register.ToArray(); changed[^2] ^= 1;
        if ((await reports.CompareWalletsAsync(world.Manifest, [row], ct, (_, _) => Task.FromResult<ReadOnlyMemory<byte>>(changed))).Matched) throw new InvalidOperationException("Native retained history tamper accepted.");
    }
}
