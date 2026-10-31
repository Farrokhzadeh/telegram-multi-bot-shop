using Npgsql;
using Platform.Persistence;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class SupportSchemaTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string UserA = "00000000-0000-0000-0000-000000000101";
    private const string UserB = "00000000-0000-0000-0000-000000000102";
    private const string UserA2 = "00000000-0000-0000-0000-000000000103";
    private const string AdminA = "00000000-0000-0000-0000-000000000301";
    private const string OrderA = "00000000-0000-0000-0000-000000000401";
    private const string OrderA2 = "00000000-0000-0000-0000-000000000403";

    [Fact]
    public async Task TicketLifecycleStaffAssignmentAndPreorderLinkAreMembershipScoped()
    {
        await using var database = await CreateAsync();
        const string ticket = "INSERT INTO shop.support_tickets(id,user_id,category,source) VALUES ('00000000-0000-0000-0000-000000000601','" + UserA + "','general','customer')";
        await ExecuteAsync(database, ticket);
        await RejectAsync(database, "INSERT INTO shop.support_tickets(id,user_id,category,source) VALUES (gen_random_uuid(),'" + UserA2 + "','preorder_cancellation','customer')");
        await RejectAsync(database, "INSERT INTO shop.support_tickets(id,user_id,order_id,category,source) VALUES (gen_random_uuid(),'" + UserA2 + "','" + OrderA + "','preorder_cancellation','customer')");
        await ExecuteAsync(database, "UPDATE shop.support_tickets SET status='assigned',assigned_admin_id='" + AdminA + "',updated_at=CURRENT_TIMESTAMP WHERE id='00000000-0000-0000-0000-000000000601'");
        await RejectAsync(database, "UPDATE shop.support_tickets SET assigned_admin_id=gen_random_uuid() WHERE id='00000000-0000-0000-0000-000000000601'", PostgresErrorCodes.ForeignKeyViolation);
        await RejectAsync(database, "UPDATE shop.support_tickets SET status='closed' WHERE id='00000000-0000-0000-0000-000000000601'");
        await ExecuteAsync(database, "UPDATE shop.support_tickets SET status='waiting_customer',updated_at=CURRENT_TIMESTAMP WHERE id='00000000-0000-0000-0000-000000000601'");
        await ExecuteAsync(database, "UPDATE shop.support_tickets SET status='closed',closed_at=CURRENT_TIMESTAMP,updated_at=CURRENT_TIMESTAMP WHERE id='00000000-0000-0000-0000-000000000601'");
        await ExecuteAsync(database, "UPDATE shop.support_tickets SET status='open',closed_at=NULL,updated_at=CURRENT_TIMESTAMP WHERE id='00000000-0000-0000-0000-000000000601'");
        await RejectAsync(database, "UPDATE shop.support_tickets SET order_id='" + OrderA + "' WHERE id='00000000-0000-0000-0000-000000000601'");
        await RejectAsync(database, "UPDATE shop.support_tickets SET user_id='" + UserB + "' WHERE id='00000000-0000-0000-0000-000000000601'");
        await RejectAsync(database, "UPDATE shop.support_tickets SET created_at=CURRENT_TIMESTAMP WHERE id='00000000-0000-0000-0000-000000000601'");
        Assert.Equal("public-test-v1", await ScalarAsync<string>(database, "SELECT price_calculation_version FROM shop.orders WHERE id='" + OrderA + "'"));
    }

    [Fact]
    public async Task OrderLinkedPreorderCancellationTicketIsMembershipScopedAndKeepsFinancialState()
    {
        await using var database = await CreateAsync();
        const string ticket = "INSERT INTO shop.support_tickets(id,user_id,order_id,category,source) VALUES ('00000000-0000-0000-0000-000000000604','" + UserA2 + "','" + OrderA2 + "','preorder_cancellation','customer')";
        await ExecuteAsync(database, ticket);
        await RejectAsync(database, "UPDATE shop.support_tickets SET user_id='" + UserA + "' WHERE id='00000000-0000-0000-0000-000000000604'");
        Assert.Equal("paid", await ScalarAsync<string>(database, "SELECT payment_status FROM shop.orders WHERE id='" + OrderA2 + "'"));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM shop.wallet_entries"));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM shop.discount_uses"));
    }

    [Fact]
    public async Task SupportMessagesAreAppendOnlySenderScopedAndMediaCapable()
    {
        await using var database = await CreateAsync();
        const string ticket = "INSERT INTO shop.support_tickets(id,user_id,order_id,category,source) VALUES ('00000000-0000-0000-0000-000000000602','" + UserA2 + "','" + OrderA2 + "','preorder_cancellation','customer')";
        await ExecuteAsync(database, ticket);
        await ExecuteAsync(database, """
            INSERT INTO shop.support_messages(id,ticket_id,sender_kind,sender_telegram_id,body_ciphertext,body_key_id)
            VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000602','customer',7002,decode('01','hex'),'public-synthetic-key')
            """);
        await RejectAsync(database, """
            INSERT INTO shop.support_messages(id,ticket_id,sender_kind,sender_telegram_id,body_ciphertext,body_key_id)
            VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000602','customer',7099,decode('02','hex'),'public-synthetic-key')
            """);
        await ExecuteAsync(database, $$"""
            INSERT INTO shop.support_messages(id,ticket_id,sender_kind,sender_admin_id,body_ciphertext,body_key_id)
            VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000602','admin','{{AdminA}}',decode('03','hex'),'public-synthetic-key')
            """);
        await RejectAsync(database, """
            INSERT INTO shop.support_messages(id,ticket_id,sender_kind,body_ciphertext,body_key_id)
            VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000602','admin',decode('04','hex'),'public-synthetic-key')
            """);
        await RejectAsync(database, """
            INSERT INTO shop.support_messages(id,ticket_id,sender_kind,body_key_id)
            VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000602','system','public-synthetic-key')
            """);
        await RejectAsync(database, """
            INSERT INTO shop.support_messages(id,ticket_id,sender_kind)
            VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000602','system')
            """);
        await ExecuteAsync(database, """
            INSERT INTO shop.support_messages(id,ticket_id,sender_kind,asset_ref)
            VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000602','system','public-synthetic-media')
            """);
        await RejectAsync(database, "UPDATE shop.support_messages SET asset_ref='changed'");
        await RejectAsync(database, "DELETE FROM shop.support_messages WHERE ticket_id='00000000-0000-0000-0000-000000000602'");
        await RejectAsync(database, "TRUNCATE shop.support_messages");
    }

    [Fact]
    public async Task SupportTicketAuditReferencesRequireTheMembershipBrandScope()
    {
        await using var database = await CreateAsync();
        await ExecuteAsync(database, "INSERT INTO shop.support_tickets(id,user_id,category,source) VALUES ('00000000-0000-0000-0000-000000000603','" + UserA + "','general','admin')");
        await RejectAsync(database, $$"""
            INSERT INTO shop.audit_events(id,actor_kind,actor_admin_id,brand_id,action,entity_kind,entity_id,reason,correlation_id)
            VALUES (gen_random_uuid(),'admin','{{AdminA}}','00000000-0000-0000-0000-000000000002','support_assign','support_ticket','00000000-0000-0000-0000-000000000603','public synthetic assignment',gen_random_uuid())
            """);
        await ExecuteAsync(database, $$"""
            INSERT INTO shop.audit_events(id,actor_kind,actor_admin_id,brand_id,action,entity_kind,entity_id,reason,correlation_id)
            VALUES (gen_random_uuid(),'admin','{{AdminA}}','00000000-0000-0000-0000-000000000001','support_assign','support_ticket','00000000-0000-0000-0000-000000000603','public synthetic assignment',gen_random_uuid())
            """);
    }

    private static async Task<DisposablePostgres> CreateAsync()
    {
        var database = await DisposablePostgres.CreateAsync(Ct);
        try
        {
            await SchemaRunner.ApplyAsync(database.Data, SchemaManifest.ForQualification(database.Data.ConnectionString), Ct);
            await ExecuteTransactionAsync(database, $$"""
                INSERT INTO shop.brands(id,code,internal_name,public_name,market,default_language,bot_username,bot_id,bot_token_ciphertext,bot_token_key_id)
                VALUES ('00000000-0000-0000-0000-000000000001','sa','public-sa','public sa','iran','fa','public_sa',1001,decode('01','hex'),'public-key-a'),
                       ('00000000-0000-0000-0000-000000000002','sb','public-sb','public sb','international','en','public_sb',1002,decode('02','hex'),'public-key-b');
                INSERT INTO shop.users(id,brand_id,telegram_id,language) VALUES
                    ('{{UserA}}','00000000-0000-0000-0000-000000000001',7001,'fa'),
                    ('{{UserB}}','00000000-0000-0000-0000-000000000002',7001,'en'),
                    ('{{UserA2}}','00000000-0000-0000-0000-000000000001',7002,'fa');
                INSERT INTO shop.admins(id,login,authentication_mode,password_verifier)
                    VALUES ('{{AdminA}}','public-support-admin','password','public-synthetic-verifier');
                INSERT INTO shop.products(id,fulfillment_kind,availability_policy,title) VALUES
                    ('00000000-0000-0000-0000-000000000201','manual','immediate','public synthetic product');
                INSERT INTO shop.brand_products(brand_id,product_id,price_usdt) VALUES
                    ('00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000201',10);
                """ + OrderSql(OrderA, UserA) + OrderSql(OrderA2, UserA2, true));
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private static string OrderSql(string id, string user, bool paid = false) => $$"""
        INSERT INTO shop.orders(id,user_id,product_id,quantity,order_kind,payment_status,currency,payment_method,
            unit_price_usdt,auto_price_toman_snapshot,pricing_rounding_step_snapshot,price_calculation_version,
            brand_public_name_snapshot,product_title_snapshot,payment_terms_snapshot,fulfillment_kind_snapshot,local_first_snapshot,is_preorder,payment_due_at,payment_confirmed_at)
        VALUES ('{{id}}','{{user}}','00000000-0000-0000-0000-000000000201',2,'product_purchase','{{(paid ? "paid" : "awaiting_payment")}}','USDT','{{(paid ? "wallet" : "crypto")}}',
            10,false,5000,'public-test-v1','public test brand','public test product','public test terms','manual',true,false,
            {{(paid ? "NULL" : "CURRENT_TIMESTAMP+INTERVAL '30 minutes'")}},{{(paid ? "CURRENT_TIMESTAMP" : "NULL")}});
        """;

    private static async Task ExecuteAsync(DisposablePostgres database, string sql)
    {
        await using var command = database.Data.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task ExecuteTransactionAsync(DisposablePostgres database, string sql)
    {
        await using var connection = await database.Data.OpenConnectionAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(Ct);
        await transaction.CommitAsync(Ct);
    }

    private static async Task RejectAsync(DisposablePostgres database, string sql, string sqlState = PostgresErrorCodes.CheckViolation)
    {
        await using var connection = await database.Data.OpenConnectionAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        var error = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync(Ct);
            await transaction.CommitAsync(Ct);
        });
        Assert.Equal(sqlState, error.SqlState);
    }

    private static async Task<T> ScalarAsync<T>(DisposablePostgres database, string sql)
    {
        await using var command = database.Data.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }
}
