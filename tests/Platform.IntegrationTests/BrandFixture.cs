using System.Security.Claims;
using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

internal sealed class BrandFixture : IAsyncDisposable
{
    internal DisposablePostgres Database { get; }
    internal DbSessionFactory Sessions { get; }
    internal Guid[] Brands { get; } = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
    internal Guid[] Users { get; } = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
    internal Guid[] Orders { get; } = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
    internal Guid Product { get; } = Guid.NewGuid();
    internal Guid PrivateProduct { get; } = Guid.NewGuid();
    internal Guid OtherUser { get; } = Guid.NewGuid();
    internal BrandContext[] Contexts { get; private set; } = [];
    private BrandFixture(DisposablePostgres database) { Database = database; Sessions = new(database.Data); }

    internal static async Task<BrandFixture> CreateAsync(CancellationToken ct, Guid[]? brands = null, Guid? deploymentId = null, long revision = 1)
    {
        var database = await DisposablePostgres.CreateAsync(ct);
        try
        {
            await Platform.Persistence.SchemaRunner.ApplyAsync(database.Data, SchemaManifest.ForQualification(database.Data.ConnectionString), ct);
            var fixture = new BrandFixture(database);
            if (brands is not null) Array.Copy(brands, fixture.Brands, 3);
            var deployment = deploymentId ?? Guid.NewGuid();
            var registry = new BotBrandRegistry(fixture.Brands.Select((brand, i) => new BotBrandBinding("test-bot", "bot" + i, brand, 1001 + i, deployment, revision, true)));
            fixture.Contexts = Enumerable.Range(0, 3).Select(i =>
            {
                var identity = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "bot" + i)], "test-bot"));
                if (!registry.TryResolveAuthenticated("test-bot", identity, out var context)) throw new InvalidOperationException();
                return context!;
            }).ToArray();
            await using var seed = await fixture.Sessions.BeginAsync(ct);
            await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.products(id,fulfillment_kind,availability_policy,title) VALUES ($1,'manual','immediate','public shared'),($2,'manual','immediate','public private')",
                DbValue.Uuid(fixture.Product), DbValue.Uuid(fixture.PrivateProduct)), ct);
            for (int i = 0; i < 3; i++)
            {
                await seed.ExecuteAsync(new DbStatement("""
                    INSERT INTO shop.brands(id,code,internal_name,public_name,market,default_language,bot_username,bot_id,bot_token_ciphertext,bot_token_key_id,active)
                    VALUES ($1,$2,$2,$2,'international',$3,$2,$4,$5,'public-key',true)
                    """, DbValue.Uuid(fixture.Brands[i]), DbValue.Text("public-brand-" + i), DbValue.Text(i == 0 ? "fa" : "en"), DbValue.Bigint(1001 + i), DbValue.Binary([1])), ct);
                await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.users(id,brand_id,telegram_id,language) VALUES ($1,$2,7001,$3)",
                    DbValue.Uuid(fixture.Users[i]), DbValue.Uuid(fixture.Brands[i]), DbValue.Text(i == 0 ? "fa" : "en")), ct);
                await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.wallets(user_id) VALUES ($1)", DbValue.Uuid(fixture.Users[i])), ct);
                await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.brand_products(brand_id,product_id,price_usdt) VALUES ($1,$2,10)",
                    DbValue.Uuid(fixture.Brands[i]), DbValue.Uuid(fixture.Product)), ct);
                await seed.ExecuteAsync(new DbStatement("""
                    INSERT INTO shop.orders(id,user_id,product_id,quantity,order_kind,payment_status,currency,payment_method,unit_price_usdt,
                        auto_price_toman_snapshot,pricing_rounding_step_snapshot,price_calculation_version,brand_public_name_snapshot,
                        product_title_snapshot,payment_terms_snapshot,fulfillment_kind_snapshot,local_first_snapshot,is_preorder,payment_due_at)
                    VALUES ($1,$2,$3,1,'product_purchase','awaiting_payment','USDT','crypto',10,false,5000,'public-v1',
                        'public brand','public product','public terms','manual',true,false,CURRENT_TIMESTAMP+INTERVAL '30 minutes')
                    """, DbValue.Uuid(fixture.Orders[i]), DbValue.Uuid(fixture.Users[i]), DbValue.Uuid(fixture.Product)), ct);
            }
            await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.users(id,brand_id,telegram_id,language) VALUES ($1,$2,7002,'en')",
                DbValue.Uuid(fixture.OtherUser), DbValue.Uuid(fixture.Brands[0])), ct);
            await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.brand_products(brand_id,product_id,price_usdt) VALUES ($1,$2,10)",
                DbValue.Uuid(fixture.Brands[1]), DbValue.Uuid(fixture.PrivateProduct)), ct);
            await seed.CommitAsync(ct);
            return fixture;
        }
        catch { await database.DisposeAsync(); throw; }
    }

    public ValueTask DisposeAsync() => Database.DisposeAsync();
}
