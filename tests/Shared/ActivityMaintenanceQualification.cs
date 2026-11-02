using Npgsql;
using Platform.Persistence;

namespace Platform.Testing;

public static class ActivityMaintenanceQualification
{
    public static async Task RunAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: ct);
        SchemaManifest.ForQualification(f.Database.Data.ConnectionString).Validate();
        await using (var seed = await f.Sessions.BeginAsync(ct))
        {
            await TelegramReplayStore.TryClaimAsync(seed, f.Context(0), 10, ct);
            await seed.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.bot_activity_events(id,user_id,event_type,source_kind,action_code,telegram_update_id,outcome_code,idempotency_key,occurred_at)
                VALUES(gen_random_uuid(),$1,'navigation','command','start',10,'accepted','telegram:10',CURRENT_TIMESTAMP-INTERVAL '181 days'),
                    (gen_random_uuid(),$1,'navigation','command','start',NULL,'accepted','public-maintenance-recent',CURRENT_TIMESTAMP)
                """, DbValue.Uuid(f.Users[0])), ct);
            await seed.CommitAsync(ct);
        }
        string before = await WorkflowActivityQualification.DomainAsync(f.Sessions, true, ct);
        string role = "shop_activity_" + Guid.NewGuid().ToString("N");
        await using (var grant = f.Database.Data.CreateCommand($"CREATE ROLE {role} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT; GRANT {role} TO shop_test_runner; GRANT USAGE ON SCHEMA shop TO {role}; GRANT SELECT(id,occurred_at),DELETE ON shop.bot_activity_events TO {role}; GRANT EXECUTE ON FUNCTION shop.activity_retention_covered(uuid),shop.activity_cleanup_candidates(integer),shop.require_true(boolean,text) TO {role}")) await grant.ExecuteNonQueryAsync(ct);
        try
        {
            var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SHOP_TEST_POSTGRES_ADMIN") ?? throw new InvalidOperationException("Synthetic control identity is required."))
            { Database = new NpgsqlConnectionStringBuilder(f.Database.Data.ConnectionString).Database, Options = "-c role=" + role, IncludeErrorDetail = false, Pooling = false };
            SchemaManifest.ForQualification(options.ConnectionString).Validate();
            await using var data = new NpgsqlSlimDataSourceBuilder(options.ConnectionString).Build();
            await using (var identity = data.CreateCommand("SELECT current_user::text")) Require((string)(await identity.ExecuteScalarAsync(ct))! == role, "actual restricted database identity");
            var cleanup = await new BotActivityRetentionStore(new(data)).CleanAsync(1, ct);
            Require(cleanup.Deleted == 1 && cleanup.Backlog.Expired == 0, "restricted role cleans only independently proven expired activity");
            foreach (string sql in new[] { "SELECT balance FROM shop.wallets", "SELECT * FROM shop.orders", "SELECT * FROM shop.payments", "SELECT * FROM shop.audit_events", "SELECT user_id FROM shop.bot_activity_events", "UPDATE shop.bot_activity_events SET occurred_at=clock_timestamp()", "TRUNCATE shop.bot_activity_events", "DELETE FROM shop.orders", "INSERT INTO shop.bot_activity_events(id) VALUES(gen_random_uuid())" })
            {
                bool denied = false;
                await using var command = data.CreateCommand(sql);
                try { await command.ExecuteNonQueryAsync(ct); } catch (PostgresException e) when (e.SqlState == "42501") { denied = true; }
                Require(denied, "maintenance cannot read private domains or write/rewrite unrelated records");
            }
            bool recent = false;
            await using (var command = data.CreateCommand("DELETE FROM shop.bot_activity_events"))
            { try { await command.ExecuteNonQueryAsync(ct); } catch (PostgresException e) when (e.SqlState == "23514") { recent = true; } }
            Require(recent && before == await WorkflowActivityQualification.DomainAsync(f.Sessions, true, ct), "expiry guard and all 27 domain relations remain intact under maintenance credentials");
        }
        finally
        {
            await using var drop = f.Database.Data.CreateCommand($"DROP OWNED BY {role}; DROP ROLE {role}"); await drop.ExecuteNonQueryAsync(ct);
        }
    }
    private static void Require(bool value, string stage) { if (!value) throw new InvalidOperationException("Activity maintenance qualification failed: " + stage + "."); }
}
