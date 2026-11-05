using System.Security.Cryptography;
using Npgsql;
using Platform.Migration;
using Platform.Persistence;
using Platform.Testing;
namespace Platform.IntegrationTests;

[Collection("PostgreSQL races")]
public sealed class LegacyProductSourceTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("price")]
    [InlineData("stock")]
    [InlineData("media")]
    [InlineData("inactive")]
    [InlineData("removed")]
    [InlineData("schema")]
    public async Task ProductReadRequiresActiveCommittedSource(string fault)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await CheckoutFixture.CreateAsync(users: 0, stock: 0, ct: ct);
        await using (var setup = f.Database.Data.CreateCommand("""
            CREATE TABLE public.django_migrations(id bigint PRIMARY KEY,app text,name text);
            CREATE TABLE public.products(id bigint PRIMARY KEY,name text,price numeric(18,2),price_irr numeric(24,2),auto_price boolean,description text,rules text,icon_custom_emoji_id text,post_purchase text,product_type text,active boolean,is_removed boolean,pricing_rate_id bigint,post_purchase_media_type text,post_purchase_media_id text,expected_availability_date date,service_target_hours integer,stock integer);
            INSERT INTO public.django_migrations VALUES(1,'store','0011_preorder');
            INSERT INTO public.products VALUES(456,'synthetic',12.50,500000,false,NULL,NULL,NULL,NULL,'credential',true,false,NULL,NULL,NULL,NULL,NULL,2);
            """)) await setup.ExecuteNonQueryAsync(ct);
        var endpoint = new NpgsqlConnectionStringBuilder(f.Database.Data.ConnectionString);
        var reader = new LegacySourceReader(f.Database.Data, new(endpoint.Host!, endpoint.Port, endpoint.Database!, endpoint.Username!));
        byte[] schema = await reader.InspectSchemaAsync(ct);
        var row = new LegacySourceProduct(456, "synthetic", 12.50m, 500000m, false, null, null, null, null, "credential", true, false, null, null, null, null, null, 2);
        Guid target = Guid.NewGuid();
        byte[] body = VerifiedImportManifest.EncodePlan(SchemaManifest.ForQualification(f.Database.Data.ConnectionString), "public-a", f.Brands[0], "66371a23a1c8149dc5bdf89a0355aa689d454dba", schema, "protected:synthetic/source", SHA256.HashData("synthetic copy"u8), Guid.NewGuid(), [row.Proof(target, f.Brands[0])]);
        var manifest = VerifiedImportManifest.Open("protected:synthetic/manifest", SHA256.HashData(body), body);
        string? mutation = fault switch { "price" => "UPDATE public.products SET price=13", "stock" => "UPDATE public.products SET stock=3", "media" => "UPDATE public.products SET post_purchase_media_id='changed'", "inactive" => "UPDATE public.products SET active=false", "removed" => "UPDATE public.products SET is_removed=true", "schema" => "ALTER TABLE public.products ADD COLUMN extra text", _ => null };
        if (mutation is not null) { await using var change = f.Database.Data.CreateCommand(mutation); await change.ExecuteNonQueryAsync(ct); }
        if (fault == "valid") Assert.Equal(row, await reader.ReadProductAsync(manifest, row.Id, target, ct));
        else await Assert.ThrowsAsync<MigrationRejectedException>(() => reader.ReadProductAsync(manifest, row.Id, target, ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.inventory_items", ct));
    }
}
