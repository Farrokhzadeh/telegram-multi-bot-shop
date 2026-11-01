using System.Security.Cryptography;
using Platform.Persistence;
using Platform.Security;
using Platform.Telegram;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class HumanSupportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly byte[] Secret = SHA256.HashData("public synthetic support qualification"u8);
    private static readonly Lazy<string> Verifier = new(() => AdminAuth.CreatePasswordVerifier(new string('s', 20)));

    [Fact]
    public async Task HumanSupportOperatesWithoutAiAndMovesNoMoney()
    {
        await using var fixture = await BrandFixture.CreateAsync(Ct);
        var router = new HumanSupportRouter(fixture.Sessions);
        var first = await router.RouteAsync(Support(fixture, 31), Ct);
        Assert.True(first.Created);
        var replay = await router.RouteAsync(Support(fixture, 32), Ct);
        Assert.False(replay.Created);
        Assert.Equal(first.TicketId, replay.TicketId);
        var actor = await Actor(fixture);
        Guid messageId;
        await using (var session = await fixture.Sessions.BeginAsync(Ct))
        {
            messageId = await SupportStore.RecordStaffReplyAsync(session, actor, first.TicketId, "support-reply-1", ct: Ct);
            await session.CommitAsync(Ct);
        }
        Assert.Equal(("waiting_customer", actor.AdminId), await Ticket(fixture, first.TicketId));
        Assert.Equal(1L, await Count(fixture, "SELECT count(*) FROM shop.support_messages WHERE ticket_id=$1 AND sender_kind='admin'", first.TicketId));
        var delivery = await Delivery(fixture, messageId);
        Assert.Equal(("support_notice", "customer_dm", fixture.Users[0], "pending", "send_telegram", "pending"), delivery);
        await Unchanged(fixture);
    }

    [Fact]
    public async Task PreorderCancellationTicketIsMembershipScopedAndLeavesFinancialState()
    {
        await using var fixture = await BrandFixture.CreateAsync(Ct);
        Guid order = fixture.Orders[0];
        Guid adminId = Guid.NewGuid();
        await using (var seed = await fixture.Sessions.BeginAsync(Ct))
        {
            await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.admins(id,login,authentication_mode,password_verifier) VALUES ($1,'public-support-admin','password',$2)",
                DbValue.Uuid(adminId), DbValue.Text(Verifier.Value)), Ct);
            await seed.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.discounts(id,brand_id,code_digest,code_ciphertext,code_key_id,display_prefix,issuance_batch_id,kind,percent_off,created_by_admin_id)
                VALUES ($1,$2,decode(repeat('55',32),'hex'),decode('01','hex'),'public-key','PUB',gen_random_uuid(),'promotion',10,$3)
                """, DbValue.Uuid(Guid.NewGuid()), DbValue.Uuid(fixture.Brands[0]), DbValue.Uuid(adminId)), Ct);
            await seed.ExecuteAsync(new DbStatement("""
                UPDATE shop.orders SET payment_status='paid',payment_confirmed_at=CURRENT_TIMESTAMP,is_preorder=true,fulfillment_kind_snapshot='local',fulfillment_status='waiting_stock' WHERE id=$1
                """, DbValue.Uuid(order)), Ct);
            Guid discount = await seed.QuerySingleAsync(new DbStatement("SELECT id FROM shop.discounts"), row => row.GetGuid(0), Ct);
            await seed.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.discount_uses(discount_id,order_id,units_covered,saved_amount,saved_currency,status) VALUES ($1,$2,1,1,'USD','held')
                """, DbValue.Uuid(discount), DbValue.Uuid(order)), Ct);
            await seed.CommitAsync(Ct);
        }
        Guid ticketId;
        bool created;
        await using (var session = await fixture.Sessions.BeginAsync(Ct))
        {
            (ticketId, created) = await SupportStore.OpenTicketAsync(session, fixture.Contexts[0], 7001, order, Ct);
            await session.CommitAsync(Ct);
        }
        Assert.True(created);
        Assert.Equal("preorder_cancellation", await Category(fixture, ticketId));
        var actor = await Actor(fixture);
        await using (var session = await fixture.Sessions.BeginAsync(Ct))
        {
            await SupportStore.RecordStaffReplyAsync(session, actor, ticketId, "support-reply-2", ct: Ct);
            await session.CommitAsync(Ct);
        }
        Assert.Equal("paid", await OrderStatus(fixture, order));
        Assert.Equal(1L, await CountAll(fixture, "SELECT count(*) FROM shop.discount_uses WHERE status='held'"));
        Assert.Equal("waiting_stock", await Scalar(fixture, "SELECT fulfillment_status FROM shop.orders WHERE id=$1", order));
        await Unchanged(fixture);
    }

    [Fact]
    public async Task OrderLinkedTicketRejectsForeignOrdersWithoutDisclosure()
    {
        await using var fixture = await BrandFixture.CreateAsync(Ct);
        Guid ownOrder = fixture.Orders[0];
        await using (var seed = await fixture.Sessions.BeginAsync(Ct))
        {
            await seed.ExecuteAsync(new DbStatement("""
                UPDATE shop.orders SET payment_status='paid',payment_confirmed_at=CURRENT_TIMESTAMP,is_preorder=true,fulfillment_kind_snapshot='local',fulfillment_status='waiting_stock' WHERE id=$1
                """, DbValue.Uuid(ownOrder)), Ct);
            await seed.CommitAsync(Ct);
        }
        Guid ticketId;
        bool created;
        await using (var session = await fixture.Sessions.BeginAsync(Ct))
        {
            (ticketId, created) = await SupportStore.OpenTicketAsync(session, fixture.Contexts[0], 7001, ownOrder, Ct);
            await session.CommitAsync(Ct);
        }
        Assert.True(created);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => OpenInNewSession(fixture, session =>
            SupportStore.OpenTicketAsync(session, fixture.Contexts[0], 7001, fixture.Orders[1], Ct)));
        Assert.Equal(0L, await Count(fixture, "SELECT count(*) FROM shop.support_tickets WHERE order_id=$1", fixture.Orders[1]));
    }

    [Fact]
    public async Task StaffAssignmentIsIdempotentAndClosedTicketsStopRepliesUntilReopened()
    {
        await using var fixture = await BrandFixture.CreateAsync(Ct);
        Guid ticketId;
        await using (var session = await fixture.Sessions.BeginAsync(Ct))
        {
            (ticketId, _) = await SupportStore.OpenTicketAsync(session, fixture.Contexts[0], 7001, null, Ct);
            await session.CommitAsync(Ct);
        }
        var actor = await Actor(fixture);
        await using (var session = await fixture.Sessions.BeginAsync(Ct))
        {
            Assert.Equal(ticketId, await SupportStore.AssignTicketAsync(session, actor, ticketId, AuditReason.OperatorReview, Ct));
            await session.CommitAsync(Ct);
        }
        await using (var session = await fixture.Sessions.BeginAsync(Ct))
        {
            Assert.Equal(ticketId, await SupportStore.AssignTicketAsync(session, actor, ticketId, AuditReason.OperatorReview, Ct));
            await session.CommitAsync(Ct);
        }
        Assert.Equal(1L, await Count(fixture, "SELECT count(*) FROM shop.audit_events WHERE action='support_assign' AND entity_id=$1", ticketId));
        await using (var session = await fixture.Sessions.BeginAsync(Ct))
        {
            await SupportStore.CloseTicketAsync(session, actor, ticketId, Ct);
            await session.CommitAsync(Ct);
        }
        Assert.Equal(("closed", actor.AdminId), await Ticket(fixture, ticketId));
        Guid messageId;
        await using (var session = await fixture.Sessions.BeginAsync(Ct))
        {
            await SupportStore.ReopenTicketAsync(session, actor, ticketId, Ct);
            messageId = await SupportStore.RecordStaffReplyAsync(session, actor, ticketId, "support-reply-3", ct: Ct);
            await session.CommitAsync(Ct);
        }
        Assert.Equal(("waiting_customer", actor.AdminId), await Ticket(fixture, ticketId));
        Assert.True(await Count(fixture, "SELECT count(*) FROM shop.telegram_deliveries WHERE support_message_id=$1 AND status='pending'", messageId) >= 1);
    }

    private static async Task<AuthenticatedAdmin> Actor(BrandFixture fixture)
    {
        await SeedAdmin(fixture);
        using var ticket = await Login(fixture);
        AuthenticatedAdmin actor;
        await using (var resolve = await fixture.Sessions.BeginAsync(Ct))
        {
            actor = await AdminSessionStore.AuthenticateAsync(resolve, ticket.Token, Ct);
            await resolve.CommitAsync(Ct);
        }
        return actor;
    }

    private static async Task SeedAdmin(BrandFixture fixture)
    {
        Guid admin = Guid.NewGuid();
        await using (var seed = await fixture.Sessions.BeginAsync(Ct))
        {
            await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.admins(id,login,authentication_mode,password_verifier) VALUES ($1,'public-support-admin','password',$2)",
                DbValue.Uuid(admin), DbValue.Text(Verifier.Value)), Ct);
            await seed.CommitAsync(Ct);
        }
    }

    private static async Task<IssuedAdminSession> Login(BrandFixture fixture)
    {
        await using var login = await fixture.Sessions.BeginAsync(Ct);
        var ticket = await AdminSessionStore.LoginAsync(login, "public-support-admin", new string('s', 20).AsMemory(), TimeSpan.FromHours(1), Ct);
        try { await login.CommitAsync(Ct); return ticket; }
        catch { ticket.Dispose(); throw; }
    }

    private static async Task<T> OpenInNewSession<T>(BrandFixture fixture, Func<DbSession, Task<T>> action)
    {
        await using var session = await fixture.Sessions.BeginAsync(Ct);
        return await action(session);
    }

    private static async Task<(string Status, Guid? AssignedAdminId)> Ticket(BrandFixture fixture, Guid ticketId)
    {
        await using var read = await fixture.Sessions.BeginAsync(Ct);
        var row = await read.QuerySingleAsync<(string Status, Guid? AssignedAdminId)>(new DbStatement("SELECT status,assigned_admin_id FROM shop.support_tickets WHERE id=$1", DbValue.Uuid(ticketId)),
            r => (r.GetString(0), r.IsDBNull(1) ? null : r.GetGuid(1)), Ct);
        await read.CommitAsync(Ct);
        return row;
    }

    private static async Task<string> Category(BrandFixture fixture, Guid ticketId)
    {
        await using var read = await fixture.Sessions.BeginAsync(Ct);
        string category = await read.QuerySingleAsync(new DbStatement("SELECT category FROM shop.support_tickets WHERE id=$1", DbValue.Uuid(ticketId)), r => r.GetString(0), Ct);
        await read.CommitAsync(Ct);
        return category;
    }

    private static async Task<string> OrderStatus(BrandFixture fixture, Guid orderId)
    {
        await using var read = await fixture.Sessions.BeginAsync(Ct);
        string status = await read.QuerySingleAsync(new DbStatement("SELECT payment_status FROM shop.orders WHERE id=$1", DbValue.Uuid(orderId)), r => r.GetString(0), Ct);
        await read.CommitAsync(Ct);
        return status;
    }

    private static async Task<string> Scalar(BrandFixture fixture, string sql, Guid parameter)
    {
        await using var read = await fixture.Sessions.BeginAsync(Ct);
        string value = await read.QuerySingleAsync(new DbStatement(sql, DbValue.Uuid(parameter)), r => r.GetString(0), Ct);
        await read.CommitAsync(Ct);
        return value;
    }

    private static async Task<long> Count(BrandFixture fixture, string sql, Guid parameter)
    {
        await using var read = await fixture.Sessions.BeginAsync(Ct);
        long count = await read.QuerySingleAsync(new DbStatement(sql, DbValue.Uuid(parameter)), r => r.GetInt64(0), Ct);
        await read.CommitAsync(Ct);
        return count;
    }

    private static async Task<long> CountAll(BrandFixture fixture, string sql)
    {
        await using var read = await fixture.Sessions.BeginAsync(Ct);
        long count = await read.QuerySingleAsync(new DbStatement(sql), r => r.GetInt64(0), Ct);
        await read.CommitAsync(Ct);
        return count;
    }

    private static async Task<(string Purpose, string RecipientKind, Guid UserId, string Status, string JobKind, string JobStatus)> Delivery(BrandFixture fixture, Guid messageId)
    {
        await using var read = await fixture.Sessions.BeginAsync(Ct);
        var row = await read.QuerySingleAsync(new DbStatement("""
            SELECT d.purpose,d.recipient_kind,d.user_id,d.status,j.job_kind,j.status FROM shop.telegram_deliveries d
            LEFT JOIN shop.jobs j ON j.telegram_delivery_id=d.id WHERE d.support_message_id=$1
            """, DbValue.Uuid(messageId)), r => (r.GetString(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.IsDBNull(4) ? "" : r.GetString(4), r.IsDBNull(5) ? "" : r.GetString(5)), Ct);
        await read.CommitAsync(Ct);
        return row;
    }

    private static async Task Unchanged(BrandFixture fixture)
    {
        await using var read = await fixture.Sessions.BeginAsync(Ct);
        Assert.Equal(0L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.wallet_entries"), r => r.GetInt64(0), Ct));
        Assert.Equal(0L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.inventory_items"), r => r.GetInt64(0), Ct));
        Assert.Equal(0L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.fulfillments"), r => r.GetInt64(0), Ct));
        await read.CommitAsync(Ct);
    }

    private static VerifiedTelegramUpdate Support(BrandFixture fixture, long updateId)
    {
        using var verifier = new TelegramWebhookVerifier(fixture.Contexts[0], Secret);
        return verifier.Verify([TelegramFixture.Header(Secret)], TelegramFixture.Body("/support", updateId: updateId, actorId: 7001, chatId: 7001));
    }
}
