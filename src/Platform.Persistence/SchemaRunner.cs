using System.Data;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Platform.Persistence;

public sealed record SchemaReceipt(string Revision, string SqlSha256, string CatalogSha256, bool Applied);

// Called only by the explicit operator tool and disposable qualification probes.
// No API/bot/job startup path invokes this class.
public static class SchemaRunner
{
    private const long MigrationLock = 1454270701;
    private static readonly string[] Tables = [
        "admin_sessions", "admins", "audit_events", "bot_activity_events", "brand_channels", "brand_products", "brands",
        "campaigns", "discount_revisions", "discount_uses", "discounts", "fulfillments", "inventory_items", "jobs",
        "legacy_id_map", "orders", "payments", "product_stock_subscriptions", "product_supplier_sources", "products",
        "supplier_request_attempts", "supplier_requests", "support_messages", "support_tickets", "telegram_deliveries", "users",
        "wallet_entries", "wallets"
    ];

    public static string BaselineSql { get; } = ReadBaseline();
    public static string BaselineSqlSha256 { get; } = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(BaselineSql)));

    public static async Task<SchemaReceipt> ApplyAsync(NpgsqlDataSource data, SchemaManifest manifest,
        CancellationToken cancellationToken = default)
    {
        manifest.Validate();
        var options = new NpgsqlConnectionStringBuilder(data.ConnectionString);
        if (System.Environment.GetEnvironmentVariable("SHOP_TEST_ALLOW_DATABASE_CREATION") != "1" ||
            options.Host != manifest.Host || options.Port != manifest.Port || options.Database != manifest.Database || options.Username != manifest.Username ||
            !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(manifest.SqlSha256), Convert.FromHexString(BaselineSqlSha256)))
        {
            throw new InvalidOperationException("Schema identity or reviewed SQL digest mismatch.");
        }

        await using var connection = await data.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await ExecuteAsync(connection, transaction,
            "SET LOCAL lock_timeout='5s'; SET LOCAL statement_timeout='60s'; SET LOCAL search_path=pg_catalog; SET LOCAL TIME ZONE 'UTC'", cancellationToken);
        await using (var identity = new NpgsqlCommand("SELECT current_database(),current_user,current_setting('server_version_num'),pg_get_userbyid(datdba) FROM pg_database WHERE datname=current_database()", connection, transaction))
        await using (var reader = await identity.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            if (reader.GetString(0) != manifest.Database || reader.GetString(1) != manifest.Username ||
                reader.GetString(2) != manifest.ServerVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) || reader.GetString(3) != manifest.Username)
            {
                throw new InvalidOperationException("Connected database identity mismatch.");
            }
        }

        await ExecuteAsync(connection, transaction, $"SELECT pg_advisory_xact_lock({MigrationLock})", cancellationToken);
        object? stored;
        await using (var query = new NpgsqlCommand("SELECT obj_description(oid,'pg_namespace') FROM pg_namespace WHERE nspname='shop'", connection, transaction))
        {
            stored = await query.ExecuteScalarAsync(cancellationToken);
        }

        bool applied = stored is null;
        if (applied)
        {
            await using var empty = new NpgsqlCommand("SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_toast%' AND n.nspname NOT LIKE 'pg_temp_%' AND c.relkind IN ('r','p','v','m','f','S')", connection, transaction);
            if ((long)(await empty.ExecuteScalarAsync(cancellationToken))! != 0)
            {
                throw new InvalidOperationException("Initial schema requires an empty disposable database.");
            }
            await ExecuteAsync(connection, transaction, BaselineSql, cancellationToken);
        }

        await ExecuteAsync(connection, transaction, "SET LOCAL search_path=pg_catalog", cancellationToken);
        string catalogSha256 = await VerifyCatalogAsync(connection, transaction, cancellationToken);
        string marker = "shop-schema|0001|" + BaselineSqlSha256 + "|" + catalogSha256;
        if (applied)
        {
            // Only fixed text and locally computed hexadecimal digests enter this utility statement.
            await ExecuteAsync(connection, transaction, "COMMENT ON SCHEMA shop IS '" + marker + "'", cancellationToken);
        }
        else if (stored is not string text || !string.Equals(text, marker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Schema revision or catalog drift detected; no repair was attempted.");
        }

        await transaction.CommitAsync(cancellationToken);
        return new SchemaReceipt("0001", BaselineSqlSha256, catalogSha256, applied);
    }

    public static async Task<SchemaReceipt> InspectReadOnlyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct = default)
    {
        await using var check = new NpgsqlCommand("SELECT current_setting('transaction_read_only')='on' AND current_setting('transaction_isolation')='repeatable read',obj_description(oid,'pg_namespace') FROM pg_namespace WHERE nspname='shop'", connection, transaction);
        string marker;
        await using (var reader = await check.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct) || !reader.GetBoolean(0) || reader.IsDBNull(1)) throw new InvalidOperationException("Read-only schema identity rejected.");
            marker = reader.GetString(1); if (await reader.ReadAsync(ct)) throw new InvalidOperationException("Read-only schema identity rejected.");
        }
        string prefix = "shop-schema|0001|" + BaselineSqlSha256 + "|";
        if (!marker.StartsWith(prefix, StringComparison.Ordinal) || marker.Length != prefix.Length + 64 || !marker[prefix.Length..].All(char.IsAsciiHexDigit)) throw new InvalidOperationException("Read-only schema identity rejected.");
        return new("0001", BaselineSqlSha256, await VerifyCatalogAsync(connection, transaction, ct), false);
    }

    private static async Task<string> VerifyCatalogAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        var actual = new List<string>();
        await using (var command = new NpgsqlCommand("SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='shop' AND c.relkind IN ('r','p','v','m','f','S') ORDER BY c.relname COLLATE \"C\"", connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken)) actual.Add(reader.GetString(0));
        }
        if (actual.Count != 28 || !actual.SequenceEqual(Tables, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Canonical table registry mismatch.");
        }

        const string invalidSql = """
            WITH RECURSIVE column_types AS (
                SELECT a.atttypid AS type_id,a.attndims FROM pg_attribute a JOIN pg_class c ON c.oid=a.attrelid
                JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='shop' AND c.relkind='r' AND a.attnum>0 AND NOT a.attisdropped
                UNION
                SELECT coalesce(nullif(t.typbasetype,0),nullif(t.typelem,0)),ct.attndims FROM column_types ct
                JOIN pg_type t ON t.oid=ct.type_id WHERE t.typbasetype<>0 OR t.typelem<>0
            )
            SELECT (SELECT count(*) FROM column_types ct JOIN pg_type t ON t.oid=ct.type_id
                WHERE ct.attndims<>0 OR t.typname IN ('json','jsonb','float4','float8','money'))
                + (SELECT count(*) FROM pg_constraint con JOIN pg_namespace n ON n.oid=con.connamespace
                    WHERE n.nspname='shop' AND con.contype IN ('f','c') AND NOT con.convalidated)
                + (SELECT count(*) FROM pg_trigger tr JOIN pg_class c ON c.oid=tr.tgrelid
                    JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='shop' AND tr.tgenabled<>'O')
            """;
        await using (var command = new NpgsqlCommand(invalidSql, connection, transaction))
        {
            if ((long)(await command.ExecuteScalarAsync(cancellationToken))! != 0)
            {
                throw new InvalidOperationException("Forbidden type or unvalidated constraint.");
            }
        }

        const string fingerprintSql = """
            SELECT line FROM (
                SELECT 'T|'||c.relname||'|'||c.relkind::text||'|'||c.relrowsecurity::text AS line FROM pg_class c
                    JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='shop' AND c.relkind IN ('r','p','v','m','f','S')
                UNION ALL
                SELECT 'C|'||c.relname||'|'||a.attnum::text||'|'||a.attname||'|'||format_type(a.atttypid,a.atttypmod)||'|'||a.attnotnull::text||'|'||a.attgenerated::text||'|'||coalesce(pg_get_expr(d.adbin,d.adrelid),'')
                    FROM pg_attribute a JOIN pg_class c ON c.oid=a.attrelid JOIN pg_namespace n ON n.oid=c.relnamespace
                    LEFT JOIN pg_attrdef d ON d.adrelid=a.attrelid AND d.adnum=a.attnum
                    WHERE n.nspname='shop' AND c.relkind='r' AND a.attnum>0 AND NOT a.attisdropped
                UNION ALL
                SELECT 'K|'||coalesce(c.relname,t.typname)||'|'||con.conname||'|'||pg_get_constraintdef(con.oid)||'|'||con.convalidated::text
                    FROM pg_constraint con JOIN pg_namespace n ON n.oid=con.connamespace
                    LEFT JOIN pg_class c ON c.oid=con.conrelid LEFT JOIN pg_type t ON t.oid=con.contypid WHERE n.nspname='shop'
                UNION ALL
                SELECT 'I|'||c.relname||'|'||pg_get_indexdef(i.indexrelid)||'|'||i.indisvalid::text||'|'||i.indisready::text
                    FROM pg_index i JOIN pg_class c ON c.oid=i.indrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='shop'
                UNION ALL
                SELECT 'G|'||c.relname||'|'||tr.tgenabled::text||'|'||pg_get_triggerdef(tr.oid)
                    FROM pg_trigger tr JOIN pg_class c ON c.oid=tr.tgrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='shop' AND NOT tr.tgisinternal
                UNION ALL
                SELECT 'R|'||c.relname||'|'||con.conname||'|'||tr.tgtype::text||'|'||tr.tgenabled::text
                    FROM pg_trigger tr JOIN pg_class c ON c.oid=tr.tgrelid JOIN pg_namespace n ON n.oid=c.relnamespace
                    JOIN pg_constraint con ON con.oid=tr.tgconstraint WHERE n.nspname='shop' AND tr.tgisinternal
                UNION ALL
                SELECT 'F|'||p.proname||'|'||pg_get_functiondef(p.oid)||'|'||coalesce(p.proacl::text,'')
                    FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='shop'
                UNION ALL
                SELECT 'D|'||t.typname||'|'||format_type(t.typbasetype,t.typtypmod)||'|'||t.typnotnull::text
                    FROM pg_type t JOIN pg_namespace n ON n.oid=t.typnamespace WHERE n.nspname='shop' AND t.typtype='d'
                UNION ALL
                SELECT 'Y|'||t.typname||'|'||t.typtype::text||'|'||coalesce(format_type(nullif(t.typbasetype,0),t.typtypmod),'')||'|'||coalesce(format_type(nullif(t.typelem,0),-1),'')||'|'||
                    coalesce((SELECT string_agg(e.enumlabel,'|' ORDER BY e.enumsortorder) FROM pg_enum e WHERE e.enumtypid=t.oid),'')
                    FROM pg_type t JOIN pg_namespace n ON n.oid=t.typnamespace WHERE n.nspname='shop'
                UNION ALL
                SELECT 'A|'||c.relname||'|'||coalesce(c.relacl::text,'') FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='shop' AND c.relkind='r'
                UNION ALL
                SELECT 'N|'||coalesce(nspacl::text,'') FROM pg_namespace WHERE nspname='shop'
            ) catalog ORDER BY line COLLATE "C"
            """;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var catalog = new NpgsqlCommand(fingerprintSql, connection, transaction);
        await using var rows = await catalog.ExecuteReaderAsync(cancellationToken);
        while (await rows.ReadAsync(cancellationToken)) hash.AppendData(Encoding.UTF8.GetBytes(rows.GetString(0) + "\n"));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ReadBaseline()
    {
        using var stream = typeof(SchemaRunner).Assembly.GetManifestResourceStream("Platform.Persistence.Migrations.0001_core.sql")
            ?? throw new InvalidOperationException("Embedded schema baseline is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
