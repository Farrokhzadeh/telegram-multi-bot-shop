using Npgsql;
using Platform.Migration;
namespace Platform.SchemaRunner;

internal static class ReadOnlySourceRole
{
    internal static Task RequireAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct) => ReadOnlyReconciliationRole.RequireAsync(connection, transaction, "teleg_source_reader", ct);
}
