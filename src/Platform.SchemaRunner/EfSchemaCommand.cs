using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Platform.Persistence;
using Platform.Persistence.Migrations;

namespace Platform.SchemaRunner;

public sealed record EfSchemaDescription(string Provider, string Migration, string ScriptSha256);

public static partial class EfSchemaCommand
{
    internal static (string Ddl, string Model) DescribePlain(string provider)
    {
        using var data = PlainModel(provider);
        if (data.Database.GetMigrations().Single() != PlainContentBaseline.MigrationId || data.Database.HasPendingModelChanges())
            throw new InvalidOperationException("The provider plaintext baseline differs from the EF model.");
        return (Convert.ToHexStringLower(PlainTargetSchema.ScriptDigest(data)), Convert.ToHexStringLower(PlainTargetSchema.ModelDigest(data)));
    }

    private static PlainTargetDbContext PlainModel(string provider) => provider switch
    {
        "sqlite" => new SqlitePlainDesignTimeFactory().CreateDbContext([]),
        "postgres" => new PostgresPlainDesignTimeFactory().CreateDbContext([]),
        _ => throw new ArgumentException("The EF provider is unsupported.", nameof(provider))
    };

    public static EfSchemaDescription Describe(string provider)
    {
        using AkantiDbContext context = provider switch
        {
            "sqlite" => new SqliteDesignTimeFactory().CreateDbContext([]),
            "postgres" => new PostgresDesignTimeFactory().CreateDbContext([]),
            _ => throw new ArgumentException("The EF provider is unsupported.", nameof(provider))
        };
        string[] migrations = context.Database.GetMigrations().ToArray();
        if (migrations.Length == 0 || context.Database.HasPendingModelChanges())
            throw new InvalidOperationException("The EF migration model is not qualified for a schema description.");
        string script = context.GetService<IMigrator>().GenerateScript().Replace("\r\n", "\n", StringComparison.Ordinal);
        return new(provider, migrations[^1], Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(script))));
    }
}
