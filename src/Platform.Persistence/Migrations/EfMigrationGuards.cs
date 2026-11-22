namespace Platform.Persistence.Migrations;

internal static class EfMigrationGuards
{
    internal static string SqliteBaseline => Read("Sqlite");
    internal static string PostgresBaseline => Read("PostgreSql");
    private static string Read(string provider)
    {
        using var stream = typeof(EfMigrationGuards).Assembly.GetManifestResourceStream(
            "Platform.Persistence.EfMigrations." + provider + ".0001_ef_integrity.sql")
            ?? throw new InvalidOperationException("Reviewed EF migration integrity definitions are unavailable.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
