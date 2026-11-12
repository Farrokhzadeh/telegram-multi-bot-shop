using Npgsql;
using Platform.Persistence;
namespace Platform.Migration;

public static class ReadOnlyReconciliationRole
{
    public const string Query = """
        SELECT current_user=$1 AND r.rolcanlogin AND NOT r.rolsuper AND NOT r.rolcreatedb AND NOT r.rolcreaterole AND NOT r.rolreplication AND NOT r.rolbypassrls
        AND NOT EXISTS(SELECT 1 FROM pg_auth_members m WHERE m.member=r.oid)
        AND NOT EXISTS(SELECT 1 FROM pg_shdepend d WHERE d.refclassid='pg_authid'::regclass AND d.refobjid=r.oid AND d.deptype='o')
        AND NOT has_database_privilege(current_user,current_database(),'CREATE,TEMPORARY')
        AND NOT EXISTS(SELECT 1 FROM pg_namespace n WHERE n.nspname !~ '^pg_' AND n.nspname<>'information_schema' AND has_schema_privilege(current_user,n.oid,'CREATE'))
        AND NOT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname !~ '^pg_' AND n.nspname<>'information_schema' AND c.relkind IN ('r','p','v','m','f') AND (c.relowner=r.oid OR has_table_privilege(current_user,c.oid,'INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')))
        AND NOT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname !~ '^pg_' AND n.nspname<>'information_schema' AND c.relkind='S' AND (c.relowner=r.oid OR has_sequence_privilege(current_user,c.oid,'USAGE,UPDATE')))
        AND NOT EXISTS(SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname !~ '^pg_' AND n.nspname<>'information_schema' AND (p.proowner=r.oid OR p.prosecdef AND has_function_privilege(current_user,p.oid,'EXECUTE')))
        FROM pg_roles r WHERE r.rolname=current_user
        """;
    public static async Task RequireAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string username, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(Query, connection, transaction); command.Parameters.AddWithValue(username);
        if (await command.ExecuteScalarAsync(ct) is not true) throw new MigrationRejectedException();
    }
    internal static async Task RequireAsync(DbSession session, string username, CancellationToken ct)
    {
        if (!await session.QuerySingleAsync(new DbStatement(Query, DbValue.Text(username)), r => r.GetBoolean(0), ct)) throw new MigrationRejectedException();
    }
}
