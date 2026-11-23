namespace Platform.Persistence.Migrations;

internal static class EfMigrationGuards
{
    internal static string SqliteBaseline => Read("Sqlite");
    internal static string PostgresBaseline => Read("PostgreSql");
    internal static IReadOnlyList<(string Name, string Sql)> SqliteTriggers()
    {
        var triggers = new List<(string, string)>();
        foreach (string body in SqliteBaseline.Split("CREATE TRIGGER ", StringSplitOptions.RemoveEmptyEntries))
        {
            int boundary = body.IndexOf(' ');
            if (boundary <= 0) throw new InvalidOperationException("A reviewed SQLite trigger definition is invalid.");
            string name = body[..boundary];
            if (!name.All(value => char.IsAsciiLetterOrDigit(value) || value == '_'))
                throw new InvalidOperationException("A reviewed SQLite trigger name is invalid.");
            triggers.Add((name, "CREATE TRIGGER " + body.Trim()));
        }
        return triggers;
    }
    private static string Read(string provider)
    {
        using var stream = typeof(EfMigrationGuards).Assembly.GetManifestResourceStream(
            "Platform.Persistence.EfMigrations." + provider + ".0001_ef_integrity.sql")
            ?? throw new InvalidOperationException("Reviewed EF migration integrity definitions are unavailable.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
