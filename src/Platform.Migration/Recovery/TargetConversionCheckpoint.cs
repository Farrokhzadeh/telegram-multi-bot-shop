using Microsoft.Data.Sqlite;

namespace Platform.Migration.Recovery;

internal static class TargetConversionCheckpoint
{
    internal static async Task CompleteAsync(SqliteConnection destination, byte[] binding, int assets, bool verifyOnly, CancellationToken ct)
    {
        await using var transaction = destination.BeginTransaction(deferred: verifyOnly);
        await using var command = destination.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT asset_count FROM _akanti_row_copy WHERE id=1 AND binding=$binding AND row_count IS NOT NULL";
        command.Parameters.Add("$binding", SqliteType.Blob).Value = binding;
        object? previous = await command.ExecuteScalarAsync(ct);
        if (previous is DBNull && !verifyOnly)
        {
            command.CommandText = "UPDATE _akanti_row_copy SET asset_count=$assets WHERE id=1 AND binding=$binding AND asset_count IS NULL";
            command.Parameters.Add("$assets", SqliteType.Integer).Value = assets;
            if (await command.ExecuteNonQueryAsync(ct) != 1) throw new MigrationRejectedException();
        }
        else if (previous is not long count || count != assets) throw new MigrationRejectedException();
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
    }
}
