using System.Security.Cryptography;
using Npgsql;
using Platform.Migration;
using Platform.Persistence;
using Platform.Testing;
namespace Platform.IntegrationTests;

[Collection("PostgreSQL races")]
public sealed class LegacyDiscountSourceTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("uses")]
    [InlineData("owner")]
    [InlineData("product")]
    [InlineData("active")]
    [InlineData("schema")]
    public async Task SourceDiscountRequiresCommittedPrivateIdentityAndCounters(string fault)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await CheckoutFixture.CreateAsync(users: 0, stock: 0, ct: ct);
        await using (var setup = f.Database.Data.CreateCommand("""
            CREATE TABLE public.django_migrations(id bigint PRIMARY KEY,app text,name text);
            CREATE TABLE public.discounts(code varchar(64) PRIMARY KEY,product_id bigint,assigned_user_id bigint,pct_off numeric(6,2) NOT NULL,max_uses integer NOT NULL,current_uses integer NOT NULL,active boolean NOT NULL);
            INSERT INTO public.django_migrations VALUES(1,'store','0005_discount_assigned_user');
            INSERT INTO public.discounts VALUES('SYNTHETIC-PRIVATE-CODE',456,123,100,10,3,true);
            """)) await setup.ExecuteNonQueryAsync(ct);
        var endpoint = new NpgsqlConnectionStringBuilder(f.Database.Data.ConnectionString);
        var reader = new LegacySourceReader(f.Database.Data, new(endpoint.Host!, endpoint.Port, endpoint.Database!, endpoint.Username!));
        byte[] schema = await reader.InspectSchemaAsync(ct);
        var row = new LegacySourceDiscount("SYNTHETIC-PRIVATE-CODE", 456, 123, 100m, 10, 3, true);
        var target = Guid.NewGuid(); var context = f.Context(0); var proof = row.Proof(target, context, f.Secrets);
        Assert.DoesNotContain(row.Code, proof.LegacyId, StringComparison.Ordinal);
        Assert.Equal("LegacySourceDiscount [redacted]", row.ToString());
        byte[] body = VerifiedImportManifest.EncodePlan(SchemaManifest.ForQualification(f.Database.Data.ConnectionString), "public-a", f.Brands[0], "66371a23a1c8149dc5bdf89a0355aa689d454dba", schema, "protected:synthetic/source", SHA256.HashData("synthetic copy"u8), Guid.NewGuid(), [proof]);
        var manifest = VerifiedImportManifest.Open("protected:synthetic/manifest", SHA256.HashData(body), body);
        string? mutation = fault switch { "uses" => "UPDATE public.discounts SET current_uses=4", "owner" => "UPDATE public.discounts SET assigned_user_id=NULL", "product" => "UPDATE public.discounts SET product_id=NULL", "active" => "UPDATE public.discounts SET active=false", "schema" => "ALTER TABLE public.discounts ADD COLUMN extra text", _ => null };
        if (mutation is not null) { await using var change = f.Database.Data.CreateCommand(mutation); await change.ExecuteNonQueryAsync(ct); }
        if (fault == "valid") Assert.Equal(row, await reader.ReadDiscountAsync(context, f.Secrets, manifest, row.Code, target, ct));
        else await Assert.ThrowsAsync<MigrationRejectedException>(() => reader.ReadDiscountAsync(context, f.Secrets, manifest, row.Code, target, ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.legacy_id_map", ct));
    }
}
