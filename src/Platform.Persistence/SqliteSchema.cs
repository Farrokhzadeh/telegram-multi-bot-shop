using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Platform.Core;

namespace Platform.Persistence;

public static class SqliteSchema
{
    private const string UtcFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    public static async Task InitializePlaintextAsync(string path, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An absolute SQLite database path is required.", nameof(path));
        ct.ThrowIfCancellationRequested();
        var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (new FileStream(path, fileOptions)) { }
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false, ForeignKeys = true, DefaultTimeout = 5 }.ToString());
        await connection.OpenAsync(ct);
        Configure(connection);
        await using var command = connection.CreateCommand(); command.CommandText = "PRAGMA journal_mode=WAL";
        if (!string.Equals(await command.ExecuteScalarAsync(ct) as string, "wal", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Could not enable WAL for the SQLite database.");
        await using var data = new SqlitePlainTargetDbContext(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<SqlitePlainTargetDbContext>().UseSqlite(connection).Options);
        await Migrations.PlainTargetSchema.CreateAsync(data, ct);
    }

    public static async Task InitializeAsync(string path, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An absolute SQLite database path is required.", nameof(path));
        ct.ThrowIfCancellationRequested();
        var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (new FileStream(path, fileOptions)) { }

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 5
        }.ToString());
        await connection.OpenAsync(ct);
        Configure(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL";
        if (!string.Equals(await command.ExecuteScalarAsync(ct) as string, "wal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Could not enable WAL for the SQLite database.");

        await using var transaction = connection.BeginTransaction(deferred: false);
        using var schema = typeof(SqliteSchema).Assembly.GetManifestResourceStream("Platform.Persistence.Migrations.Sqlite.0001_core.sql")
            ?? throw new InvalidOperationException("The SQLite schema resource is missing.");
        using var reader = new StreamReader(schema);
        command.Transaction = transaction;
        command.CommandText = await reader.ReadToEndAsync(ct);
        await command.ExecuteNonQueryAsync(ct);
        command.CommandText = "PRAGMA user_version=1";
        await command.ExecuteNonQueryAsync(ct);
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
    }

    internal static string UtcText(DateTime value) => value.Kind == DateTimeKind.Utc
        ? value.ToString(UtcFormat, CultureInfo.InvariantCulture)
        : throw new ArgumentException("A UTC timestamp is required.", nameof(value));

    public static Task ValidateIntegrityAsync(SqliteTransaction transaction, CancellationToken ct = default) =>
        SqliteIntegrity.ValidateAsync(transaction.Connection ?? throw new InvalidOperationException("A live SQLite transaction is required."), transaction, ct);

    public static void Configure(SqliteConnection connection)
    {
        connection.CreateFunction("akanti_utc_now", () => UtcText(DateTime.UtcNow), isDeterministic: false);
        connection.CreateFunction<string?, bool>("akanti_uuid", value => value?.Length == 36 && Guid.TryParseExact(value, "D", out _), isDeterministic: true);
        connection.CreateFunction<string?, bool>("akanti_utc_time", value =>
            DateTime.TryParseExact(value, UtcFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _), isDeterministic: true);
        connection.CreateFunction<string, int, string>("akanti_add_minutes", (value, minutes) =>
            DateTime.ParseExact(value, UtcFormat, CultureInfo.InvariantCulture).AddMinutes(minutes)
                .ToString(UtcFormat, CultureInfo.InvariantCulture), isDeterministic: true);
        connection.CreateFunction<string, string, string>("akanti_decimal_add", (left, right) =>
            ExactDecimal.Format(checked(ExactDecimal.Parse(left) + ExactDecimal.Parse(right))), isDeterministic: true);
        connection.CreateFunction<string?, bool>("akanti_decimal", value => TryCanonicalDecimal(value, out _), isDeterministic: true);
        connection.CreateFunction<string?, string, int?>("akanti_decimal_compare", (left, right) =>
            TryCanonicalDecimal(left, out var a) && TryCanonicalDecimal(right, out var b) ? decimal.Compare(a, b) : null, isDeterministic: true);
    }

    private static bool TryCanonicalDecimal(string? value, out decimal result) =>
        decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out result) &&
        string.Equals(value, ExactDecimal.Format(result), StringComparison.Ordinal);
}
