using Microsoft.Data.Sqlite;
using Platform.Migration;
using Platform.SchemaRunner;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class EfQualificationTests
{
    [Fact]
    public async Task PostgresQualificationCreatesOnlyAnOwnedEmptyTargetWithTheReviewedScript()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await DisposablePostgres.CreateAsync(ct);
        string hash = EfSchemaCommand.Describe("postgres").ScriptSha256;
        var receipt = await EfSchemaCommand.CreatePostgresQualificationAsync(database.Data, hash, ct);
        Assert.Equal(hash, receipt.ScriptSha256);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => EfSchemaCommand.CreatePostgresQualificationAsync(database.Data, hash, ct));
    }

    [Fact]
    public async Task QualificationAdoptionBindsTheWholeSchemaAndPreservesRowsAcrossReplay()
    {
        var ct = TestContext.Current.CancellationToken;
        var description = EfSchemaCommand.Describe("sqlite");
        await Assert.ThrowsAsync<InvalidOperationException>(() => EfSchemaCommand.CreateSqliteQualificationAsync(new string('0', 64), ct));
        var created = await EfSchemaCommand.CreateSqliteQualificationAsync(description.ScriptSha256, ct);
        try
        {
            Guid id = Guid.NewGuid();
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = created.Path,
                Mode = SqliteOpenMode.ReadWrite,
                ForeignKeys = true,
                Pooling = false
            }.ToString()))
            {
                await connection.OpenAsync(ct);
                Platform.Persistence.SqliteSchema.Configure(connection);
                await using var command = connection.CreateCommand();
                command.CommandText = "DROP TABLE \"__EFMigrationsHistory\"; INSERT INTO products(id,title,fulfillment_kind,availability_policy) VALUES($id,'Retained fixture','manual','immediate')";
                command.Parameters.AddWithValue("$id", id);
                await command.ExecuteNonQueryAsync(ct);
            }
            var adopted = await EfSchemaCommand.AdoptSqliteQualificationAsync(created.Path, description.ScriptSha256, ct);
            Assert.True(adopted.HistoryWritten);
            Assert.Equal(created.SchemaSha256, adopted.SchemaSha256);
            Assert.False((await EfSchemaCommand.AdoptSqliteQualificationAsync(created.Path, description.ScriptSha256, ct)).HistoryWritten);
            await using var verify = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = created.Path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await verify.OpenAsync(ct);
            await using var read = verify.CreateCommand();
            read.CommandText = "SELECT title FROM products WHERE id=$id";
            read.Parameters.AddWithValue("$id", id);
            Assert.Equal("Retained fixture", await read.ExecuteScalarAsync(ct));
        }
        finally { Directory.Delete(Path.GetDirectoryName(created.Path)!, true); }
    }

    [Fact]
    public async Task QualificationAdoptionRejectsSchemaDriftAndSymbolicLinksBeforeStamping()
    {
        var ct = TestContext.Current.CancellationToken;
        var description = EfSchemaCommand.Describe("sqlite");
        var created = await EfSchemaCommand.CreateSqliteQualificationAsync(description.ScriptSha256, ct);
        var links = Directory.CreateTempSubdirectory("akanti-ef-qualification-");
        try
        {
            string link = Path.Combine(links.FullName, "shop.db");
            File.CreateSymbolicLink(link, created.Path);
            await Assert.ThrowsAsync<MigrationRejectedException>(() => EfSchemaCommand.AdoptSqliteQualificationAsync(link, description.ScriptSha256, ct));
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = created.Path,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString()))
            {
                await connection.OpenAsync(ct);
                await using var command = connection.CreateCommand();
                command.CommandText = "DROP TABLE \"__EFMigrationsHistory\"; CREATE TABLE unexpected(note TEXT)";
                await command.ExecuteNonQueryAsync(ct);
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => EfSchemaCommand.AdoptSqliteQualificationAsync(created.Path, description.ScriptSha256, ct));
            await using var verify = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = created.Path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await verify.OpenAsync(ct);
            await using var read = verify.CreateCommand();
            read.CommandText = "SELECT count(*) FROM sqlite_schema WHERE name='__EFMigrationsHistory'";
            Assert.Equal(0L, await read.ExecuteScalarAsync(ct));
        }
        finally
        {
            links.Delete(recursive: true);
            Directory.Delete(Path.GetDirectoryName(created.Path)!, true);
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QualificationAdoptionRejectsForeignMigrationHistoryWithoutRewritingIt(bool foreignMigration)
    {
        var ct = TestContext.Current.CancellationToken;
        var description = EfSchemaCommand.Describe("sqlite");
        var created = await EfSchemaCommand.CreateSqliteQualificationAsync(description.ScriptSha256, ct);
        try
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = created.Path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            await connection.OpenAsync(ct);
            await using var change = connection.CreateCommand();
            change.CommandText = foreignMigration ? "UPDATE __EFMigrationsHistory SET MigrationId='foreign_migration'" : "UPDATE __EFMigrationsHistory SET ProductVersion='0.0.0'";
            Assert.Equal(1, await change.ExecuteNonQueryAsync(ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => EfSchemaCommand.AdoptSqliteQualificationAsync(created.Path, description.ScriptSha256, ct));
            change.CommandText = foreignMigration ? "SELECT count(*) FROM __EFMigrationsHistory WHERE MigrationId='foreign_migration'" : "SELECT count(*) FROM __EFMigrationsHistory WHERE ProductVersion='0.0.0'";
            Assert.Equal(1L, await change.ExecuteScalarAsync(ct));
        }
        finally { Directory.Delete(Path.GetDirectoryName(created.Path)!, true); }
    }

}
