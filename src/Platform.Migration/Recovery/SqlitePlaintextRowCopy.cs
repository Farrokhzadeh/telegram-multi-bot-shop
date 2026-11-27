using System.Data;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Platform.Persistence;
using Platform.Persistence.Migrations;
using Platform.Security;

namespace Platform.Migration.Recovery;

internal sealed class SqlitePlaintextRowCopy(SecretSnapshot keys, Func<Guid, BrandContext> contextFor, Action? batchCommitted = null)
{
    internal async Task<long> TransferAsync(SqliteConnection source, SqliteConnection destination, ReadOnlyMemory<byte> expectedSnapshot, bool verifyOnly, CancellationToken ct, ReadOnlyMemory<byte> binding = default, ReadOnlyMemory<byte> expectedTargetSchema = default)
    {
        var before = new SqliteConnectionStringBuilder(source.ConnectionString);
        var after = new SqliteConnectionStringBuilder(destination.ConnectionString);
        if (expectedSnapshot.Length != 32 || (!binding.IsEmpty && binding.Length != 32) || (!expectedTargetSchema.IsEmpty && expectedTargetSchema.Length != 32) ||
            before.Mode != SqliteOpenMode.ReadOnly || before.Pooling || after.Pooling ||
            after.Mode != (verifyOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite) ||
            source.State != ConnectionState.Open || destination.State != ConnectionState.Open ||
            ReferenceEquals(source, destination) || !Path.IsPathFullyQualified(before.DataSource) ||
            !Path.IsPathFullyQualified(after.DataSource) || Path.GetFullPath(before.DataSource) == Path.GetFullPath(after.DataSource))
            throw new MigrationRejectedException();

        if (!verifyOnly && !binding.IsEmpty)
            return await TransferBatchesAsync(source, destination, expectedSnapshot, binding, expectedTargetSchema, ct);

        await using var snapshot = source.BeginTransaction(deferred: true);
        await using var write = destination.BeginTransaction(deferred: verifyOnly);
        await using var old = new SqliteRecoveryTargetDbContext(new DbContextOptionsBuilder<SqliteRecoveryTargetDbContext>().UseSqlite(source).Options);
        await using var next = new PlainTargetCopyDbContext(new DbContextOptionsBuilder<PlainTargetCopyDbContext>().UseSqlite(destination).Options);
        await old.Database.UseTransactionAsync(snapshot, ct);
        await next.Database.UseTransactionAsync(write, ct);
        await RequireSchemaAsync(old, snapshot, 1, ct);
        await RequireSchemaAsync(next, write, PlainTargetSchema.SqliteVersion, ct);
        byte[] observed = await SnapshotAsync(old, snapshot, ct);
        if (!CryptographicOperations.FixedTimeEquals(observed, expectedSnapshot.Span)) throw new MigrationRejectedException();
        long? copied = await ReadCheckpointAsync(destination, write, expectedSnapshot, binding, expectedTargetSchema, ct);
        if (verifyOnly && copied is null) throw new MigrationRejectedException();
        bool resume = copied.HasValue;
        if (!resume)
        {
            await using var defer = destination.CreateCommand();
            defer.Transaction = write;
            defer.CommandText = "PRAGMA defer_foreign_keys=ON";
            await defer.ExecuteNonQueryAsync(ct);
        }

        if (!resume) await ConfigureHistoricalJobGuardAsync(destination, write, true, ct);
        var transform = new TargetPlaintextRows(keys, contextFor);
        long count = 0;
        foreach (var entity in old.Model.GetEntityTypes().OrderBy(entity => entity.GetTableName() == "jobs").ThenBy(entity => entity.GetTableName(), StringComparer.Ordinal))
        {
            if (!resume && await SnapshotCopyQueries.CountAsync(next, entity.GetTableName()!, ct) != 0)
                throw new MigrationRejectedException();
            count = checked(count + await SnapshotCopyQueries.CopyOrVerifyAsync(old, next, entity,
                (from, to, token) => transform.CopyAsync(old, next, from, to, token, track: false), resume, ct));
        }
        if (!resume) await ConfigureHistoricalJobGuardAsync(destination, write, false, ct);
        await TargetOrderManifests.VerifyAsync(next, ct);
        await SqliteIntegrity.ValidateAsync(destination, write, ct);
        if (resume)
        {
            if (count != copied) throw new MigrationRejectedException();
        }
        else
        {
            await using var checkpoint = destination.CreateCommand();
            checkpoint.Transaction = write;
            checkpoint.CommandText = "UPDATE _akanti_row_copy SET snapshot=$snapshot,row_count=$count,binding=$binding WHERE id=1 AND snapshot IS NULL";
            checkpoint.Parameters.Add("$snapshot", SqliteType.Blob).Value = observed;
            checkpoint.Parameters.Add("$count", SqliteType.Integer).Value = count;
            checkpoint.Parameters.Add("$binding", SqliteType.Blob).Value = binding.IsEmpty ? DBNull.Value : binding.ToArray();
            if (await checkpoint.ExecuteNonQueryAsync(ct) != 1) throw new MigrationRejectedException();
        }
        ct.ThrowIfCancellationRequested();
        await write.CommitAsync(CancellationToken.None);
        return count;
    }

    private async Task<long> TransferBatchesAsync(SqliteConnection source, SqliteConnection destination,
        ReadOnlyMemory<byte> expectedSnapshot, ReadOnlyMemory<byte> binding, ReadOnlyMemory<byte> expectedSchema, CancellationToken ct)
    {
        await using var snapshot = source.BeginTransaction(deferred: true);
        await using var old = new SqliteRecoveryTargetDbContext(new DbContextOptionsBuilder<SqliteRecoveryTargetDbContext>().UseSqlite(source).Options);
        await using var next = new PlainTargetCopyDbContext(new DbContextOptionsBuilder<PlainTargetCopyDbContext>().UseSqlite(destination).Options);
        await old.Database.UseTransactionAsync(snapshot, ct);
        await RequireSchemaAsync(old, snapshot, 1, ct);
        if (!CryptographicOperations.FixedTimeEquals(await SnapshotAsync(old, snapshot, ct), expectedSnapshot.Span))
            throw new MigrationRejectedException();
        long? completed;
        await using (var begin = destination.BeginTransaction(deferred: false))
        {
            await RequireSchemaAsync(next, begin, PlainTargetSchema.SqliteVersion, ct);
            completed = await ReadCheckpointAsync(destination, begin, expectedSnapshot, binding, expectedSchema, ct);
            if (!completed.HasValue)
            {
                await using var draft = destination.CreateCommand();
                draft.Transaction = begin;
                draft.CommandText = "UPDATE _akanti_row_copy SET draft=$binding WHERE id=1 AND snapshot IS NULL AND (draft IS NULL OR draft=$binding)";
                draft.Parameters.Add("$binding", SqliteType.Blob).Value = binding.ToArray();
                if (await draft.ExecuteNonQueryAsync(ct) != 1) throw new MigrationRejectedException();
            }
            await begin.CommitAsync(ct);
        }
        var transform = new TargetPlaintextRows(keys, contextFor);
        var entities = old.Model.GetEntityTypes().OrderBy(entity => entity.GetTableName() == "jobs").ThenBy(entity => entity.GetTableName(), StringComparer.Ordinal).ToArray();
        if (!completed.HasValue)
        {
            await SetForeignKeysAsync(destination, false, ct);
            try
            {
                foreach (var entity in entities)
                {
                    bool jobs = entity.GetTableName() == "jobs";
                    await SnapshotCopyQueries.CopyOrVerifyAsync(old, next, entity,
                        (from, to, token) => transform.CopyAsync(old, next, from, to, token, track: false), false, ct,
                        async (step, token) =>
                        {
                            await using var batch = destination.BeginTransaction(deferred: false);
                            await next.Database.UseTransactionAsync(batch, token);
                            if (jobs) await ConfigureHistoricalJobGuardAsync(destination, batch, true, token);
                            await step(token);
                            if (jobs) await ConfigureHistoricalJobGuardAsync(destination, batch, false, token);
                            token.ThrowIfCancellationRequested();
                            await batch.CommitAsync(CancellationToken.None);
                            await next.Database.UseTransactionAsync(null, CancellationToken.None);
                            batchCommitted?.Invoke();
                        });
                }
            }
            finally { await SetForeignKeysAsync(destination, true, CancellationToken.None); }
        }
        await using var finish = destination.BeginTransaction(deferred: false);
        await next.Database.UseTransactionAsync(finish, ct);
        await RequireSchemaAsync(next, finish, PlainTargetSchema.SqliteVersion, ct);
        long count = 0;
        foreach (var entity in entities)
            count = checked(count + await SnapshotCopyQueries.CopyOrVerifyAsync(old, next, entity,
                (from, to, token) => transform.CopyAsync(old, next, from, to, token, track: false), true, ct));
        await TargetOrderManifests.VerifyAsync(next, ct);
        await SqliteIntegrity.ValidateAsync(destination, finish, ct);
        if (completed.HasValue)
        {
            if (completed.Value != count) throw new MigrationRejectedException();
        }
        else
        {
            await using var checkpoint = destination.CreateCommand();
            checkpoint.Transaction = finish;
            checkpoint.CommandText = "UPDATE _akanti_row_copy SET snapshot=$snapshot,row_count=$count,binding=$binding,draft=NULL WHERE id=1 AND snapshot IS NULL AND draft=$binding";
            checkpoint.Parameters.Add("$snapshot", SqliteType.Blob).Value = expectedSnapshot.ToArray();
            checkpoint.Parameters.Add("$count", SqliteType.Integer).Value = count;
            checkpoint.Parameters.Add("$binding", SqliteType.Blob).Value = binding.ToArray();
            if (await checkpoint.ExecuteNonQueryAsync(ct) != 1) throw new MigrationRejectedException();
        }
        ct.ThrowIfCancellationRequested();
        await finish.CommitAsync(CancellationToken.None);
        return count;
    }

    private static async Task SetForeignKeysAsync(SqliteConnection connection, bool enabled, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = enabled ? "PRAGMA foreign_keys=ON" : "PRAGMA foreign_keys=OFF";
        await command.ExecuteNonQueryAsync(ct);
        command.CommandText = "PRAGMA foreign_keys";
        if (Convert.ToInt64(await command.ExecuteScalarAsync(ct)) != (enabled ? 1 : 0)) throw new MigrationRejectedException();
    }

    private static async Task<long?> ReadCheckpointAsync(SqliteConnection connection, SqliteTransaction transaction,
        ReadOnlyMemory<byte> expectedSnapshot, ReadOnlyMemory<byte> binding, ReadOnlyMemory<byte> expectedTargetSchema, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA application_id";
        if (Convert.ToInt64(await command.ExecuteScalarAsync(ct)) != PlainTargetSchema.SqliteCopyApplicationId)
            throw new MigrationRejectedException();
        byte[] digest = await PlainTargetSchema.SqliteSchemaDigestAsync(connection, transaction, ct);
        if (!expectedTargetSchema.IsEmpty && !CryptographicOperations.FixedTimeEquals(digest, expectedTargetSchema.Span))
            throw new MigrationRejectedException();
        command.CommandText = "SELECT schema_digest,snapshot,row_count,binding FROM _akanti_row_copy WHERE id=1";
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct) || !CryptographicOperations.FixedTimeEquals(rows.GetFieldValue<byte[]>(0), digest))
            throw new MigrationRejectedException();
        if (rows.IsDBNull(1)) return null;
        if (!CryptographicOperations.FixedTimeEquals(rows.GetFieldValue<byte[]>(1), expectedSnapshot.Span) ||
            (binding.IsEmpty ? !rows.IsDBNull(3) : rows.IsDBNull(3) || !CryptographicOperations.FixedTimeEquals(rows.GetFieldValue<byte[]>(3), binding.Span)))
            throw new MigrationRejectedException();
        return rows.GetInt64(2);
    }

    internal static async Task<byte[]> InspectAsync(SqliteConnection source, CancellationToken ct, Action<TargetAssetReference>? visit = null)
    {
        var settings = new SqliteConnectionStringBuilder(source.ConnectionString);
        if (settings.Mode != SqliteOpenMode.ReadOnly || settings.Pooling || source.State != ConnectionState.Open ||
            !Path.IsPathFullyQualified(settings.DataSource)) throw new MigrationRejectedException();
        await using var snapshot = source.BeginTransaction(deferred: true);
        await using var data = new SqliteRecoveryTargetDbContext(new DbContextOptionsBuilder<SqliteRecoveryTargetDbContext>().UseSqlite(source).Options);
        await data.Database.UseTransactionAsync(snapshot, ct);
        await RequireSchemaAsync(data, snapshot, 1, ct);
        return await SnapshotAsync(data, snapshot, ct, visit);
    }

    private static async Task<byte[]> SnapshotAsync(DbContext data, SqliteTransaction snapshot, CancellationToken ct, Action<TargetAssetReference>? visit = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        SqliteStagingCopy.HashValue(hash, "akanti-sqlite-encrypted-snapshot-v1");
        SqliteStagingCopy.HashValue(hash, await PlainTargetSchema.SqliteSchemaDigestAsync(snapshot.Connection!, snapshot, ct));
        foreach (var entity in data.Model.GetEntityTypes().OrderBy(entity => entity.GetTableName(), StringComparer.Ordinal))
            await SnapshotCopyQueries.HashRowsAsync(data, entity, hash, ct, visit is null ? null : TargetAssetReferences.Inspect(entity, visit));
        return hash.GetHashAndReset();
    }

    private static async Task ConfigureHistoricalJobGuardAsync(SqliteConnection connection, SqliteTransaction transaction, bool historical, CancellationToken ct)
    {
        const string pending = " AND (NEW.job_kind<>'submit_supplier' OR r.status='pending')";
        var definition = EfMigrationGuards.SqliteTriggers().Single(trigger => trigger.Name == "jobs_origin_guard").Sql.TrimEnd(';');
        if (!definition.Contains(pending, StringComparison.Ordinal)) throw new MigrationRejectedException();
        var copyDefinition = definition.Replace(pending, "", StringComparison.Ordinal);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE type='trigger' AND name='jobs_origin_guard'";
        if (await command.ExecuteScalarAsync(ct) is not string actual ||
            actual.TrimEnd(';') != (historical ? definition : copyDefinition)) throw new MigrationRejectedException();
        command.CommandText = "DROP TRIGGER jobs_origin_guard";
        await command.ExecuteNonQueryAsync(ct);
        command.CommandText = historical ? copyDefinition : definition;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task RequireSchemaAsync(DbContext data, SqliteTransaction transaction, int version, CancellationToken ct)
    {
        await using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version";
        if (Convert.ToInt64(await command.ExecuteScalarAsync(ct)) != version) throw new MigrationRejectedException();
        command.CommandText = "PRAGMA foreign_keys";
        if (Convert.ToInt64(await command.ExecuteScalarAsync(ct)) != 1) throw new MigrationRejectedException();
        command.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name NOT IN ('__EFMigrationsHistory','__EFMigrationsLock')";
        var entities = data.Model.GetEntityTypes().ToDictionary(entity => entity.GetTableName()!, StringComparer.Ordinal);
        if (entities.Count != 28) throw new MigrationRejectedException();
        var tables = new HashSet<string>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) tables.Add(reader.GetString(0));
        if (version == PlainTargetSchema.SqliteVersion && !tables.Remove(PlainTargetSchema.RowCopyTable))
            throw new MigrationRejectedException();
        if (!tables.SetEquals(entities.Keys)) throw new MigrationRejectedException();

        command.CommandText = "SELECT name FROM pragma_table_xinfo($table)";
        var tableName = command.Parameters.Add("$table", SqliteType.Text);
        foreach (var (table, entity) in entities)
        {
            tableName.Value = table;
            var store = StoreObjectIdentifier.Table(table, entity.GetSchema());
            var columns = entity.GetProperties().Select(property => property.GetColumnName(store)!);
            var actual = new HashSet<string>(StringComparer.Ordinal);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) actual.Add(reader.GetString(0));
            if (!actual.SetEquals(columns)) throw new MigrationRejectedException();
        }
    }
}
