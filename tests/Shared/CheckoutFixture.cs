using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Platform.Persistence;
using Platform.Security;

namespace Platform.Testing;

public sealed class CheckoutFixture : IAsyncDisposable
{
    private readonly SyntheticSecretFiles files;
    public DisposablePostgres Database { get; }
    public DbSessionFactory Sessions { get; }
    public SecretSnapshot Secrets { get; }
    public string SecretRoot => files.Root;
    public CheckoutStore Store => new(Sessions, Secrets);
    public Guid Product { get; } = Guid.NewGuid();
    public static Guid[] BrandIds => [SyntheticSecretFiles.BrandA, SyntheticSecretFiles.BrandB, Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc")];
    public Guid[] Brands { get; } = BrandIds;
    public Guid[] Users { get; }
    public BrandContext[] Contexts { get; }
    private readonly Guid admin = Guid.NewGuid();
    private static readonly Lazy<string> Verifier = new(() => AdminAuth.CreatePasswordVerifier(new string('s', 20)));
    private CheckoutFixture(DisposablePostgres database, SyntheticSecretFiles files, SecretSnapshot secrets, int users)
    {
        Database = database; Sessions = new(database.Data); this.files = files; Secrets = secrets;
        Users = Enumerable.Range(0, users).Select(_ => Guid.NewGuid()).ToArray();
        Contexts = ResolveContexts();
    }
    public static BrandContext[] ResolveContexts()
    {
        var registry = new BotBrandRegistry(BrandIds.Select((b, i) => new BotBrandBinding("checkout-test", "bot" + i, b, 1001 + i, SyntheticSecretFiles.Deployment, 7, true)));
        return Enumerable.Range(0, 3).Select(i =>
        {
            if (!registry.TryResolveAuthenticated("checkout-test", new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "bot" + i)], "checkout-test")), out var context)) throw new InvalidOperationException();
            return context!;
        }).ToArray();
    }
    public BrandContext Context(int user) => Contexts[user % 3];
    public static long Actor(int user) => 7001 + user / 3;
    public LocalCheckoutRequest Request(int quantity = 1, decimal price = 10) => new(Guid.CreateVersion7(), Product, quantity, price);

    public static async Task<CheckoutFixture> CreateAsync(int users = 3, decimal balance = 100, int stock = 1, CancellationToken ct = default, bool seedCatalog = true)
    {
        if (!seedCatalog && (users != 0 || stock != 0)) throw new ArgumentException();
        var database = await DisposablePostgres.CreateAsync(ct);
        var files = new SyntheticSecretFiles();
        SecretSnapshot? secrets = null;
        try
        {
            Guid[] brands = BrandIds;
            files.WriteManifest(brands: brands);
            secrets = SecretFiles.Load(files.Root, "service-config.json", files.Grant(brands: brands));
            await Platform.Persistence.SchemaRunner.ApplyAsync(database.Data, SchemaManifest.ForQualification(database.Data.ConnectionString), ct);
            var fixture = new CheckoutFixture(database, files, secrets, users);
            await using (var seed = await fixture.Sessions.BeginAsync(ct))
            {
                if (seedCatalog) await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.admins(id,login,authentication_mode,password_verifier) VALUES ($1,'public-checkout-admin','password',$2)", DbValue.Uuid(fixture.admin), DbValue.Text(Verifier.Value)), ct);
                if (seedCatalog) await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.products(id,fulfillment_kind,availability_policy,title) VALUES ($1,'local','immediate','public shared item')", DbValue.Uuid(fixture.Product)), ct);
                for (int i = 0; i < 3; i++)
                {
                    await seed.ExecuteAsync(new DbStatement("""
                        INSERT INTO shop.brands(id,code,internal_name,public_name,market,default_language,bot_username,bot_id,bot_token_ciphertext,bot_token_key_id,active)
                        VALUES ($1,$2,$2,$2,'international','en',$2,$3,$4,'current',true)
                        """, DbValue.Uuid(brands[i]), DbValue.Text("public-checkout-" + i), DbValue.Bigint(1001 + i), DbValue.Binary([1])), ct);
                    if (seedCatalog) await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.brand_products(brand_id,product_id,visible,price_usdt) VALUES ($1,$2,true,10)", DbValue.Uuid(brands[i]), DbValue.Uuid(fixture.Product)), ct);
                }
                for (int i = 0; i < users; i++)
                {
                    await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.users(id,brand_id,telegram_id,language) VALUES ($1,$2,$3,'en')", DbValue.Uuid(fixture.Users[i]), DbValue.Uuid(brands[i % 3]), DbValue.Bigint(Actor(i))), ct);
                    await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.wallets(user_id) VALUES ($1)", DbValue.Uuid(fixture.Users[i])), ct);
                    if (balance > 0)
                    {
                        Guid audit = Guid.NewGuid();
                        await seed.ExecuteAsync(new DbStatement("""
                            INSERT INTO shop.audit_events(id,actor_kind,actor_admin_id,brand_id,action,entity_kind,entity_id,reason,correlation_id)
                            VALUES ($1,'admin',$2,$3,'wallet_adjustment','wallet',$4,'public synthetic fixture',gen_random_uuid())
                            """, DbValue.Uuid(audit), DbValue.Uuid(fixture.admin), DbValue.Uuid(brands[i % 3]), DbValue.Uuid(fixture.Users[i])), ct);
                        await seed.ExecuteAsync(new DbStatement("UPDATE shop.wallets SET balance=$1,version=1 WHERE user_id=$2", DbValue.Numeric(balance), DbValue.Uuid(fixture.Users[i])), ct);
                        await seed.ExecuteAsync(new DbStatement("""
                            INSERT INTO shop.wallet_entries(id,user_id,signed_amount,balance_after,posting_version,movement_kind,is_posting,idempotency_key,admin_id,audit_event_id,reason)
                            VALUES (gen_random_uuid(),$1,$2,$2,1,'admin_adjustment',true,'public-synthetic-funding',$3,$4,'public synthetic fixture')
                            """, DbValue.Uuid(fixture.Users[i]), DbValue.Numeric(balance), DbValue.Uuid(fixture.admin), DbValue.Uuid(audit)), ct);
                    }
                }
                await seed.CommitAsync(ct);
            }
            await fixture.AddStockAsync(stock, ct);
            return fixture;
        }
        catch { secrets?.Dispose(); files.Dispose(); await database.DisposeAsync(); throw; }
    }

    public async Task<AuthenticatedAdmin> AdminAsync(CancellationToken ct = default)
    {
        await using var login = await Sessions.BeginAsync(ct);
        using var ticket = await AdminSessionStore.LoginAsync(login, "public-checkout-admin", new string('s', 20).AsMemory(), TimeSpan.FromHours(1), ct);
        await login.CommitAsync(ct);
        await using var resolve = await Sessions.BeginAsync(ct);
        return await AdminSessionStore.AuthenticateAsync(resolve, ticket.Token, ct);
    }

    public async Task AddStockAsync(int count, CancellationToken ct = default)
    {
        var cipher = new InventoryCipher(Secrets);
        await using var session = await Sessions.BeginAsync(ct);
        for (int i = 0; i < count; i++)
        {
            Guid id = Guid.NewGuid();
            byte[] plain = Encoding.UTF8.GetBytes("public synthetic credential " + id.ToString("D"));
            byte[] encrypted = cipher.Encrypt(id, plain);
            CryptographicOperations.ZeroMemory(plain);
            await session.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.inventory_items(id,product_id,credential_ciphertext,credential_key_id,source_fingerprint,origin_kind)
                VALUES ($1,$2,$3,'current',$4,'local_import')
                """, DbValue.Uuid(id), DbValue.Uuid(Product), DbValue.Binary(encrypted), DbValue.Binary(SHA256.HashData(id.ToByteArray()))), ct);
        }
        await session.CommitAsync(ct);
    }
    public async Task<long> CountAsync(string sql, CancellationToken ct = default)
    {
        await using var read = await Sessions.BeginAsync(ct);
        return await read.QuerySingleAsync(new DbStatement(sql), row => row.GetInt64(0), ct);
    }
    public async ValueTask DisposeAsync() { Secrets.Dispose(); files.Dispose(); await Database.DisposeAsync(); }
}
