using System.Security.Claims;
using System.Security.Cryptography;
using Npgsql;
using Platform.Persistence;
using Platform.Security;

namespace Platform.Testing;

public static class FoundationQualification
{
    public static void VerifyIndependentInventoryKeys()
    {
        using var files = new SyntheticSecretFiles();
        using var original = SecretFiles.Load(files.Root, "service-config.json", files.Grant());
        Guid item = Guid.NewGuid();
        var inventory = new InventoryCipher(original);
        byte[] protectedValue = inventory.Encrypt(item, "public synthetic inventory"u8);
        byte[] replacement = RandomNumberGenerator.GetBytes(32);
        try { files.Write("inventory-current", replacement); }
        finally { CryptographicOperations.ZeroMemory(replacement); }
        using var wrongKeys = SecretFiles.Load(files.Root, "service-config.json", files.Grant());
        Denied(() => new InventoryCipher(wrongKeys).Decrypt(item, "current", protectedValue));
        var registry = new BotBrandRegistry([new("test-bot", "a", SyntheticSecretFiles.BrandA, 101, SyntheticSecretFiles.Deployment, 7, true)]);
        if (!registry.TryResolveAuthenticated("test-bot", new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "a")], "test-bot")), out var context)) throw new InvalidOperationException("Synthetic identity rejected.");
        byte[] brandValue = new BrandCipher(original).Encrypt(context!, "inventory_items", item, "credential_ciphertext", "public brand value"u8);
        Denied(() => inventory.Decrypt(item, "current", brandValue));
        files.WriteManifest(SecretService.Api, false);
        using var api = SecretFiles.Load(files.Root, "service-config.json", files.Grant(SecretService.Api, false));
        Denied(() => new InventoryCipher(api).Decrypt(item, "current", protectedValue));
    }

    public static async Task VerifySchemaAsync(NpgsqlDataSource data, CancellationToken cancellationToken = default)
    {
        await using var session = await new DbSessionFactory(data).BeginAsync(cancellationToken);
        long tables = await session.QuerySingleAsync(new DbStatement("""
            SELECT count(*) FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname='shop' AND c.relkind IN ('r','p')
            """), row => row.GetInt64(0), cancellationToken);
        long untyped = await session.QuerySingleAsync(new DbStatement("""
            SELECT count(*) FROM pg_catalog.pg_attribute a JOIN pg_catalog.pg_class c ON c.oid=a.attrelid
            JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace JOIN pg_catalog.pg_type t ON t.oid=a.atttypid
            LEFT JOIN pg_catalog.pg_type base ON base.oid=t.typbasetype
            WHERE n.nspname='shop' AND c.relkind IN ('r','p') AND a.attnum>0 AND NOT a.attisdropped
                AND (t.typname IN ('json','jsonb') OR t.typcategory='A' OR base.typname IN ('json','jsonb') OR base.typcategory='A')
            """), row => row.GetInt64(0), cancellationToken);
        if (tables != 28 || untyped != 0 || System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault)
            throw new InvalidOperationException("Foundation schema/AOT qualification failed.");
    }

    private static void Denied(Func<byte[]> operation)
    {
        try { var value = operation(); CryptographicOperations.ZeroMemory(value); }
        catch (Exception error) when (error is CipherRejectedException or SecretConfigurationException) { return; }
        throw new InvalidOperationException("Inventory scope/key separation failed.");
    }
}
