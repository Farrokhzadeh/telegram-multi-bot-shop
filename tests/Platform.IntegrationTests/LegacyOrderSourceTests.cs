using System.Security.Cryptography;
using Npgsql;
using Platform.Migration;
using Platform.Persistence;
using Platform.Testing;
namespace Platform.IntegrationTests;

[Collection("PostgreSQL races")]
public sealed class LegacyOrderSourceTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("snapshot")]
    [InlineData("product")]
    [InlineData("schema")]
    [InlineData("line_table")]
    public async Task OrderReadRequiresCommittedSourceAndSingleProductShape(string fault)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await CheckoutFixture.CreateAsync(users: 0, stock: 0, ct: ct);
        await using (var setup = f.Database.Data.CreateCommand("""
            CREATE TABLE public.django_migrations(id bigint PRIMARY KEY,app text,name text);
            CREATE TABLE public.orders(id varchar(64) PRIMARY KEY,user_id bigint NOT NULL,product_id bigint NOT NULL,qty integer NOT NULL,total numeric(24,2) NOT NULL,currency varchar(12) NOT NULL,created_at timestamptz NOT NULL,checkout_snapshot jsonb NOT NULL);
            INSERT INTO public.django_migrations VALUES(1,'store','0001_initial');
            INSERT INTO public.orders VALUES('synthetic-order',123,456,2,12.50,'USD','2024-01-01 00:00Z','{"name":"synthetic"}');
            """)) await setup.ExecuteNonQueryAsync(ct);
        if (fault == "line_table") { await using var setup = f.Database.Data.CreateCommand("CREATE TABLE public.order_items(id bigint PRIMARY KEY)"); await setup.ExecuteNonQueryAsync(ct); }
        var endpoint = new NpgsqlConnectionStringBuilder(f.Database.Data.ConnectionString);
        var reader = new LegacySourceReader(f.Database.Data, new(endpoint.Host!, endpoint.Port, endpoint.Database!, endpoint.Username!));
        byte[] schema = await reader.InspectSchemaAsync(ct);
        var target = Guid.NewGuid();
        var source = new LegacySourceOrder("synthetic-order", 123, 456, 2, 12.50m, "USD", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), "{\"name\": \"synthetic\"}");
        byte[] body = VerifiedImportManifest.EncodePlan(SchemaManifest.ForQualification(f.Database.Data.ConnectionString), "public-a", f.Brands[0], "66371a23a1c8149dc5bdf89a0355aa689d454dba", schema, "protected:synthetic/source", SHA256.HashData("synthetic copy"u8), Guid.NewGuid(), [source.Proof(target, f.Brands[0])]);
        var manifest = VerifiedImportManifest.Open("protected:synthetic/manifest", SHA256.HashData(body), body);
        string? mutation = fault switch { "snapshot" => "UPDATE public.orders SET checkout_snapshot='{}'", "product" => "UPDATE public.orders SET product_id=457", "schema" => "ALTER TABLE public.orders ADD COLUMN extra text", _ => null };
        if (mutation is not null) { await using var change = f.Database.Data.CreateCommand(mutation); await change.ExecuteNonQueryAsync(ct); }
        if (fault == "valid")
        {
            var result = await reader.ReadOrderAsync(manifest, source.Id, target, ct);
            Assert.Equal(source, result); Assert.Equal("LegacySourceOrder [redacted]", result.ToString());
            await Assert.ThrowsAsync<MigrationRejectedException>(() => reader.ReadOrderAsync(manifest, source.Id, Guid.NewGuid(), ct));
        }
        else await Assert.ThrowsAsync<MigrationRejectedException>(() => reader.ReadOrderAsync(manifest, source.Id, target, ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.legacy_id_map", ct));
    }
}
