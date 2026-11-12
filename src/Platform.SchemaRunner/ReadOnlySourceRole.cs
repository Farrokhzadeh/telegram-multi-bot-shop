using Npgsql;
using Platform.Migration;
namespace Platform.SchemaRunner;

internal static class ReadOnlySourceRole
{
    internal const string Query = """
        SELECT current_user='teleg_source_reader' AND r.rolcanlogin AND NOT r.rolsuper AND NOT r.rolcreatedb AND NOT r.rolcreaterole AND NOT r.rolreplication AND NOT r.rolbypassrls
        AND NOT EXISTS(SELECT 1 FROM pg_auth_members m WHERE m.member=r.oid)
        AND NOT has_database_privilege(current_user,current_database(),'CREATE,TEMPORARY')
        AND NOT EXISTS(SELECT 1 FROM pg_namespace n WHERE n.nspname !~ '^pg_' AND n.nspname<>'information_schema' AND has_schema_privilege(current_user,n.oid,'CREATE'))
        AND NOT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname !~ '^pg_' AND n.nspname<>'information_schema' AND c.relkind IN ('r','p','v','m','f') AND (c.relowner=r.oid OR has_table_privilege(current_user,c.oid,'INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')))
        AND NOT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname !~ '^pg_' AND n.nspname<>'information_schema' AND c.relkind='S' AND (c.relowner=r.oid OR has_sequence_privilege(current_user,c.oid,'USAGE,UPDATE')))
        AND NOT EXISTS(SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname !~ '^pg_' AND n.nspname<>'information_schema' AND (p.proowner=r.oid OR p.prosecdef AND has_function_privilege(current_user,p.oid,'EXECUTE')))
        FROM pg_roles r WHERE r.rolname=current_user
        """;
    internal static async Task RequireAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(Query, connection, transaction);
        if (await command.ExecuteScalarAsync(ct) is not true) throw new MigrationRejectedException();
    }
}
