using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Platform.Persistence;

namespace Platform.IntegrationTests;

public sealed class EfBaselineParityTests
{
    [Fact]
    public async Task OriginalSqliteBaselineChecksAndIndexSemanticsSurviveTheEfBaseline()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("akanti-ef-baseline-parity-");
        try
        {
            string originalPath = Path.Combine(directory.FullName, "original.db");
            await SqliteSchema.InitializeAsync(originalPath, ct);
            await using var original = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = originalPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await original.OpenAsync(ct);
            await using var target = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
            await target.OpenAsync(ct);
            SqliteSchema.Configure(target);
            await using var context = new SqliteAkantiDbContext(new DbContextOptionsBuilder<SqliteAkantiDbContext>().UseSqlite(target).Options);
            await context.GetService<IMigrator>().MigrateAsync(context.Database.GetMigrations().First(), ct);
            var expected = await TablesAsync(original, ct);
            var actual = await TablesAsync(target, ct);
            Assert.Equal(28, expected.Count);
            Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
            foreach (var table in expected)
            {
                Assert.Equal(Checks(table.Value), Checks(actual[table.Key]));
                var before = await IndexesAsync(original, table.Key, ct);
                var after = await IndexesAsync(target, table.Key, ct);
                foreach (var index in before) Assert.Contains(after, candidate => candidate.Shape == index.Shape);
                await using var key = original.CreateCommand();
                key.CommandText = "SELECT name FROM pragma_table_info($table) WHERE pk>0 ORDER BY pk";
                key.Parameters.AddWithValue("$table", table.Key);
                var primary = new List<string>();
                await using (var reader = await key.ExecuteReaderAsync(ct))
                    while (await reader.ReadAsync(ct)) primary.Add(reader.GetString(0));
                foreach (var index in after.Where(index => index.Unique && before.All(candidate => candidate.Shape != index.Shape)))
                    Assert.True(primary.All(index.Columns.Contains), "An unreviewed unique index restricts " + table.Key + ": " + index.Shape);
            }
        }
        finally { directory.Delete(true); }
    }
    private static async Task<Dictionary<string, string>> TablesAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name,sql FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name NOT IN ('__EFMigrationsHistory','__EFMigrationsLock') ORDER BY name";
        var result = new Dictionary<string, string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(reader.GetString(0), reader.GetString(1));
        return result;
    }
    private sealed record IndexShape(bool Unique, string[] Columns, string Shape);
    private static async Task<List<IndexShape>> IndexesAsync(SqliteConnection connection, string table, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name,\"unique\" FROM pragma_index_list($table)";
        command.Parameters.AddWithValue("$table", table);
        var indexes = new List<(string Name, bool Unique)>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) indexes.Add((reader.GetString(0), reader.GetBoolean(1)));
        var result = new List<IndexShape>();
        foreach (var index in indexes)
        {
            command.Parameters.Clear();
            command.CommandText = "SELECT name,desc,coll FROM pragma_index_xinfo($index) WHERE key=1 ORDER BY seqno";
            command.Parameters.AddWithValue("$index", index.Name);
            var columns = new List<string>();
            var details = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct))
                {
                    Assert.False(reader.IsDBNull(0));
                    columns.Add(reader.GetString(0));
                    details.Add(reader.GetString(0) + ":" + reader.GetInt32(1) + ":" + reader.GetString(2));
                }
            command.CommandText = "SELECT sql FROM sqlite_schema WHERE type='index' AND name=$index";
            string? sql = await command.ExecuteScalarAsync(ct) as string;
            int where = sql is null ? -1 : Keyword(sql, "WHERE", 0);
            string predicate = where < 0 ? "" : Normalize(sql![(where + 5)..]);
            result.Add(new(index.Unique, columns.ToArray(), index.Unique + "|" + string.Join(",", details) + "|" + predicate));
        }
        return result;
    }
    private static string[] Checks(string sql)
    {
        var result = new List<string>();
        int position = 0;
        while ((position = Keyword(sql, "CHECK", position)) >= 0)
        {
            int open = position + 5;
            while (char.IsWhiteSpace(sql[open])) open++;
            Assert.Equal('(', sql[open]);
            int depth = 1, end = open + 1;
            for (; depth > 0 && end < sql.Length; end++)
            {
                if (sql[end] is '\'' or '"' or '`' or '[') end = QuotedEnd(sql, end);
                else if (sql[end] == '(') depth++;
                else if (sql[end] == ')') depth--;
            }
            Assert.Equal(0, depth);
            result.Add(Normalize(sql[(open + 1)..(end - 1)]));
            position = end;
        }
        return result.Order().ToArray();
    }
    private static int Keyword(string sql, string word, int start)
    {
        for (int index = start; index <= sql.Length - word.Length; index++)
        {
            if (sql[index] is '\'' or '"' or '`' or '[') { index = QuotedEnd(sql, index); continue; }
            if ((index == 0 || !char.IsAsciiLetterOrDigit(sql[index - 1]) && sql[index - 1] != '_') &&
                sql.AsSpan(index, word.Length).Equals(word.AsSpan(), StringComparison.OrdinalIgnoreCase) &&
                (index + word.Length == sql.Length || !char.IsAsciiLetterOrDigit(sql[index + word.Length]) && sql[index + word.Length] != '_')) return index;
        }
        return -1;
    }
    private static int QuotedEnd(string sql, int start)
    {
        char close = sql[start] == '[' ? ']' : sql[start];
        for (int end = start + 1; end < sql.Length; end++)
            if (sql[end] == close)
            {
                if (end + 1 < sql.Length && sql[end + 1] == close) { end++; continue; }
                return end;
            }
        throw new InvalidOperationException("The pinned schema contains an incomplete quoted token.");
    }
    private static string Normalize(string sql)
    {
        var result = new StringBuilder();
        for (int index = 0; index < sql.Length; index++)
            if (sql[index] is '\'' or '"' or '`' or '[')
            {
                int end = QuotedEnd(sql, index);
                result.Append(sql.AsSpan(index, end - index + 1));
                index = end;
            }
            else if (!char.IsWhiteSpace(sql[index])) result.Append(sql[index]);
        return result.ToString();
    }
}
