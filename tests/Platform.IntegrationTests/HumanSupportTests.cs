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
        await using var fixture = await SecretFixture();
        using var files = new SyntheticSecretFiles();
        using var secrets = SecretFiles.Load(files.Root, "service-config.json", files.Grant());
        var router = new HumanSupportRouter(fixture.Sessions, secrets);
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
        Guid order = await SeedPaidPreorder(fixture);
        Guid adminId = Guid.NewGuid();
        await using (var seed = await fixture.Sessions.BeginAsync(Ct))
        {
            await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.admins(id,login,authentication_mode,password_verifier) VALUES ($1,'public-support-admin','password',$2)",
                DbValue.Uuid(adminId), DbValue.Text(Verifier.Value)), Ct);
            Guid discount = Guid.NewGuid(), audit = Guid.NewGuid(), revision = Guid.NewGuid();
            await seed.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.discounts(id,brand_id,code_digest,code_ciphertext,code_key_id,display_prefix,issuance_batch_id,kind,percent_off,created_by_admin_id)
                VALUES ($1,$2,decode(repeat('55',32),'hex'),decode('01','hex'),'public-key','PUB',gen_random_uuid(),'promotion',10,$3)
                """, DbValue.Uuid(discount), DbValue.Uuid(fixture.Brands[0]), DbValue.Uuid(adminId)), Ct);
            await seed.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.audit_events(id,actor_kind,actor_admin_id,brand_id,action,entity_kind,entity_id,reason,new_revision,correlation_id)
                VALUES ($1,'admin',$2,$3,'coupon_issue','discount',$4,'public coupon issue',0,gen_random_uuid())
                """, DbValue.Uuid(audit), DbValue.Uuid(adminId), DbValue.Uuid(fixture.Brands[0]), DbValue.Uuid(discount)), Ct);
            await seed.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.discount_revisions(id,discount_id,revision_no,audit_event_id,percent_off,usage_adjustment_units,redeemed_count_at_revision,held_units_at_revision,active)
                VALUES ($1,$2,0,$3,10,0,0,0,true)
                """, DbValue.Uuid(revision), DbValue.Uuid(discount), DbValue.Uuid(audit)), Ct);
            await seed.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.discount_uses(discount_id,order_id,discount_revision_id,units_covered,saved_amount,saved_currency,status) VALUES ($1,$2,$3,1,1,'USD','held')
                """, DbValue.Uuid(discount), DbValue.Uuid(order), DbValue.Uuid(revision)), Ct);
            await seed.CommitAsync(Ct);
        }
        string before = await FinancialSnapshot(fixture);
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
        await using (var lifecycle = await fixture.Sessions.BeginAsync(Ct))
        {
            await SupportStore.AssignTicketAsync(lifecycle, actor, ticketId, AuditReason.OperatorReview, Ct);
            await SupportStore.CloseTicketAsync(lifecycle, actor, ticketId, Ct);
            await SupportStore.ReopenTicketAsync(lifecycle, actor, ticketId, Ct);
            await lifecycle.CommitAsync(Ct);
        }
        Assert.Equal(before, await FinancialSnapshot(fixture));
        Assert.Equal("paid", await OrderStatus(fixture, order));
        Assert.Equal(1L, await CountAll(fixture, "SELECT count(*) FROM shop.discount_uses WHERE status='held'"));
        Assert.Equal("waiting_stock", await Scalar(fixture, "SELECT fulfillment_status FROM shop.orders WHERE id=$1", order));
        await Unchanged(fixture);
    }

    [Fact]
    public async Task OrderLinkedTicketRejectsForeignOrdersWithoutDisclosure()
    {
        await using var fixture = await BrandFixture.CreateAsync(Ct);
        Guid ownOrder = await SeedPaidPreorder(fixture);
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

    [Fact]
    public async Task VerifiedTextIsEncryptedAndCustomerMessagesNotifyStaffDurably()
    {
        await using var fixture = await SecretFixture();
        using var files = new SyntheticSecretFiles();
        using var secrets = SecretFiles.Load(files.Root, "service-config.json", files.Grant());
        var support = new HumanSupportRouter(fixture.Sessions, secrets);
        var router = new UpdateRouter(fixture.Sessions, (_, _, _) => throw new InvalidOperationException(), support);
        Guid order = await SeedPaidPreorder(fixture);
        string before = await FinancialSnapshot(fixture);
        var opened = await router.RouteAsync(Message(fixture, "/support " + order.ToString("D"), 101), Ct);
        const string text = "public synthetic cancellation discussion / متن آزمایشی";
        var reply = await router.RouteAsync(Message(fixture, text, 102), Ct);
        Assert.Equal(opened.EntityId, reply.EntityId);
        Assert.True(opened.FlowRevision > 0);
        Assert.Equal(opened.FlowRevision, reply.FlowRevision);
        await using var read = await fixture.Sessions.BeginAsync(Ct);
        var message = await read.QuerySingleAsync(new DbStatement("SELECT id,body_ciphertext,body_key_id,asset_ref FROM shop.support_messages WHERE ticket_id=$1 AND telegram_message_id=102", DbValue.Uuid(opened.EntityId!.Value)),
            row => (Id: row.GetGuid(0), Bytes: row.GetBinary(1), Key: row.GetString(2), Asset: row.IsDBNull(3)), Ct);
        Assert.True(message.Asset);
        byte[] decoded = new BrandCipher(secrets).Decrypt(fixture.Contexts[0], "support_messages", message.Id, "body_ciphertext", message.Key, message.Bytes);
        try { Assert.Equal(text, System.Text.Encoding.UTF8.GetString(decoded)); }
        finally { CryptographicOperations.ZeroMemory(decoded); }
        Assert.Throws<CipherRejectedException>(() => new BrandCipher(secrets).Decrypt(fixture.Contexts[1], "support_messages", message.Id, "body_ciphertext", message.Key, message.Bytes));
        Assert.Equal(2L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.jobs WHERE job_kind='support_notice' AND brand_id=$1", DbValue.Uuid(fixture.Brands[0])), row => row.GetInt64(0), Ct));
        Assert.Equal(before, await FinancialSnapshot(fixture));
    }

    [Fact]
    public async Task RestartAndReplayAfterClosureDoNotCreateTicketMessageOrNotification()
    {
        await using var fixture = await SecretFixture();
        using var files = new SyntheticSecretFiles();
        using var secrets = SecretFiles.Load(files.Root, "service-config.json", files.Grant());
        var update = Message(fixture, "/support", 201);
        var first = await new HumanSupportRouter(fixture.Sessions, secrets).RouteAsync(update, Ct);
        var actor = await Actor(fixture);
        await using (var close = await fixture.Sessions.BeginAsync(Ct))
        {
            await SupportStore.CloseTicketAsync(close, actor, first.TicketId, Ct);
            await close.CommitAsync(Ct);
        }
        var replay = await new HumanSupportRouter(fixture.Sessions, secrets).RouteAsync(update, Ct);
        Assert.Equal(first.TicketId, replay.TicketId);
        Assert.False(replay.Created);
        Assert.Equal("closed", (await Ticket(fixture, first.TicketId)).Status);
        Assert.Equal(1L, await CountAll(fixture, "SELECT count(*) FROM shop.support_messages"));
        Assert.Equal(1L, await CountAll(fixture, "SELECT count(*) FROM shop.jobs WHERE job_kind='support_notice'"));
        await using var worker = await fixture.Sessions.BeginAsync(Ct);
        var lease = await OutboxStore.ClaimAsync(worker, JobKind.SupportNotice, Guid.NewGuid(), TimeSpan.FromSeconds(30), Ct);
        Assert.NotNull(lease);
        Assert.Equal(fixture.Brands[0], lease.Intent.BrandId);
        await worker.CommitAsync(Ct);
    }

    [Fact]
    public async Task UnrelatedCommandsWrongFlowForeignOrdersAndBlockedCustomersCannotWriteSupport()
    {
        await using var fixture = await SecretFixture();
        using var files = new SyntheticSecretFiles();
        using var secrets = SecretFiles.Load(files.Root, "service-config.json", files.Grant());
        var router = new HumanSupportRouter(fixture.Sessions, secrets);
        await Assert.ThrowsAsync<TelegramIngressRejectedException>(() => router.RouteAsync(Message(fixture, "/menu", 301), Ct));
        await Assert.ThrowsAsync<TelegramIngressRejectedException>(() => router.RouteAsync(Message(fixture, "public text outside support", 302), Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => router.RouteAsync(Message(fixture, "/support " + fixture.Orders[1].ToString("D"), 303), Ct));
        await using (var block = await fixture.Sessions.BeginAsync(Ct))
        {
            await block.ExecuteAsync(new DbStatement("UPDATE shop.users SET blocked_at=CURRENT_TIMESTAMP WHERE id=$1", DbValue.Uuid(fixture.Users[0])), Ct);
            await block.CommitAsync(Ct);
        }
        await Assert.ThrowsAsync<TelegramIngressRejectedException>(() => router.RouteAsync(Message(fixture, "/support", 304), Ct));
        Assert.Equal(0L, await CountAll(fixture, "SELECT count(*) FROM shop.support_tickets"));
        Assert.Equal(0L, await CountAll(fixture, "SELECT count(*) FROM shop.jobs"));
    }

    [Fact]
    public async Task VerifiedMediaReferencesStayInTheSameSupportTicketWithoutDownloading()
    {
        await using var fixture = await SecretFixture();
        using var files = new SyntheticSecretFiles();
        using var secrets = SecretFiles.Load(files.Root, "service-config.json", files.Grant());
        var router = new HumanSupportRouter(fixture.Sessions, secrets);
        var ticket = await router.RouteAsync(Message(fixture, "/support", 351), Ct);
        using var verifier = new TelegramWebhookVerifier(fixture.Contexts[0], Secret);
        byte[] body = TelegramFixture.Replace(TelegramFixture.Body("public media caption", updateId: 352, messageId: 352), "\"text\":", "\"document\":{\"file_id\":\"public-synthetic-media\"},\"text\":");
        var media = verifier.Verify([TelegramFixture.Header(Secret)], body);
        await router.RouteAsync(media, Ct);
        await router.RouteAsync(media, Ct);
        Assert.Equal(1L, await Count(fixture, "SELECT count(*) FROM shop.support_messages WHERE ticket_id=$1 AND asset_ref='public-synthetic-media'", ticket.TicketId));
        await Unchanged(fixture);
    }

    [Fact]
    public async Task DisabledBrandSupportCannotOpenTickets()
    {
        await using var fixture = await SecretFixture();
        using var files = new SyntheticSecretFiles();
        using var secrets = SecretFiles.Load(files.Root, "service-config.json", files.Grant());
        await using (var disable = await fixture.Sessions.BeginAsync(Ct))
        {
            await disable.ExecuteAsync(new DbStatement("UPDATE shop.brands SET support_enabled=false,policy_version=policy_version+1 WHERE id=$1", DbValue.Uuid(fixture.Brands[0])), Ct);
            await disable.CommitAsync(Ct);
        }
        await Assert.ThrowsAsync<TelegramIngressRejectedException>(() => new HumanSupportRouter(fixture.Sessions, secrets).RouteAsync(Message(fixture, "/support", 401), Ct));
        Assert.Equal(0L, await CountAll(fixture, "SELECT count(*) FROM shop.support_tickets"));
    }

    [Fact]
    public async Task StaffReplyRequestIdentitySuppressesRepeatedDeliveryAndRevokedNoOpsFail()
    {
        await using var fixture = await BrandFixture.CreateAsync(Ct);
        var actor = await Actor(fixture);
        Guid ticket, request = Guid.NewGuid();
        await using (var open = await fixture.Sessions.BeginAsync(Ct))
        {
            (ticket, _) = await SupportStore.OpenTicketAsync(open, fixture.Contexts[0], 7001, null, Ct);
            await open.CommitAsync(Ct);
        }
        for (int i = 0; i < 2; i++)
        {
            await using var reply = await fixture.Sessions.BeginAsync(Ct);
            Assert.Equal(request, await SupportStore.RecordStaffReplyAsync(reply, actor, ticket, "public-reply", ct: Ct, requestId: request));
            await reply.CommitAsync(Ct);
        }
        Assert.Equal(1L, await CountAll(fixture, "SELECT count(*) FROM shop.telegram_deliveries"));
        await using (var close = await fixture.Sessions.BeginAsync(Ct))
        {
            await SupportStore.CloseTicketAsync(close, actor, ticket, Ct);
            await AdminSessionStore.LogoutAsync(close, actor, Ct);
            await close.CommitAsync(Ct);
        }
        await using var revoked = await fixture.Sessions.BeginAsync(Ct);
        await Assert.ThrowsAsync<AdminAuthenticationException>(() => SupportStore.CloseTicketAsync(revoked, actor, ticket, Ct));
    }

    [Fact]
    public async Task CustomerReplyCannotWriteClosedOrForeignTicketAndRollbackLeavesNoStaffWork()
    {
        await using var fixture = await BrandFixture.CreateAsync(Ct);
        var actor = await Actor(fixture);
        Guid ticket;
        await using (var open = await fixture.Sessions.BeginAsync(Ct))
        {
            (ticket, _) = await SupportStore.OpenTicketAsync(open, fixture.Contexts[0], 7001, null, Ct);
            await open.CommitAsync(Ct);
        }
        await using (var wrong = await fixture.Sessions.BeginAsync(Ct))
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => SupportStore.RecordCustomerMessageAsync(wrong, fixture.Contexts[1], 7001, ticket, "public-foreign", ct: Ct));
        await using (var rollback = await fixture.Sessions.BeginAsync(Ct))
            await SupportStore.RecordCustomerMessageAsync(rollback, fixture.Contexts[0], 7001, ticket, "public-uncommitted", ct: Ct);
        Assert.Equal(0L, await CountAll(fixture, "SELECT count(*) FROM shop.support_messages"));
        Assert.Equal(0L, await CountAll(fixture, "SELECT count(*) FROM shop.jobs"));
        await using (var close = await fixture.Sessions.BeginAsync(Ct))
        {
            await SupportStore.CloseTicketAsync(close, actor, ticket, Ct);
            await close.CommitAsync(Ct);
        }
        await using var closed = await fixture.Sessions.BeginAsync(Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => SupportStore.RecordCustomerMessageAsync(closed, fixture.Contexts[0], 7001, ticket, "public-closed", ct: Ct));
    }

    private static async Task<string> FinancialSnapshot(BrandFixture fixture)
    {
        await using var read = await fixture.Sessions.BeginAsync(Ct);
        var hashes = new List<string>();
        foreach (string table in new[] { "orders", "wallets", "wallet_entries", "inventory_items", "fulfillments", "discounts", "discount_uses", "discount_revisions", "supplier_requests" })
            hashes.Add(await read.QuerySingleAsync(new DbStatement("SELECT md5(COALESCE(string_agg(t::text,'' ORDER BY t::text),'')) FROM shop." + table + " t"), row => row.GetString(0), Ct));
        return string.Join(":", hashes);
    }

    private static async Task<BrandFixture> SecretFixture()
    {
        var fixture = await BrandFixture.CreateAsync(Ct, [SyntheticSecretFiles.BrandA, SyntheticSecretFiles.BrandB, Guid.NewGuid()], SyntheticSecretFiles.Deployment, 7);
        try
        {
            var actor = await Actor(fixture);
            AdminToken nonce;
            await using (var begin = await fixture.Sessions.BeginAsync(Ct))
            {
                nonce = (await AdminBindingStore.BeginAsync(begin, actor, 7999, TimeSpan.FromMinutes(5), Guid.NewGuid(), Guid.NewGuid(), Ct))!;
                await begin.CommitAsync(Ct);
            }
            using (nonce)
            {
                await using (var proof = await fixture.Sessions.BeginAsync(Ct))
                {
                    Assert.True(await AdminBindingStore.ObservePrivateProofAsync(proof, actor.AdminId, nonce, PrivateTelegramProof.FromVerifiedIngress(fixture.Contexts[0], 7999, 7999, false, nonce), Ct));
                    await proof.CommitAsync(Ct);
                }
                await using var complete = await fixture.Sessions.BeginAsync(Ct);
                Assert.True(await AdminBindingStore.CompleteAsync(complete, actor, 7999, actor.AuthVersion, nonce, Guid.NewGuid(), Guid.NewGuid(), Ct));
                await complete.ExecuteAsync(new DbStatement("UPDATE shop.admins SET public_contact_handle='public_support' WHERE id=$1", DbValue.Uuid(actor.AdminId)), Ct);
                await complete.ExecuteAsync(new DbStatement("UPDATE shop.brands SET support_enabled=true,customer_support_admin_id=$1,policy_version=policy_version+1 WHERE id=$2", DbValue.Uuid(actor.AdminId), DbValue.Uuid(fixture.Brands[0])), Ct);
                await complete.CommitAsync(Ct);
            }
            return fixture;
        }
        catch { await fixture.DisposeAsync(); throw; }
    }
    private static VerifiedTelegramUpdate Message(BrandFixture fixture, string text, long id)
    {
        using var verifier = new TelegramWebhookVerifier(fixture.Contexts[0], Secret);
        return verifier.Verify([TelegramFixture.Header(Secret)], TelegramFixture.Body(text, updateId: id, messageId: id));
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
            await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.admins(id,login,authentication_mode,password_verifier) VALUES ($1,'public-support-admin','password',$2) ON CONFLICT (login) DO NOTHING",
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

    private static async Task<Guid> SeedPaidPreorder(BrandFixture fixture)
    {
        Guid product = Guid.NewGuid(), orderId = Guid.NewGuid();
        await using var seed = await fixture.Sessions.BeginAsync(Ct);
        await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.products(id,fulfillment_kind,availability_policy,title) VALUES ($1,'local','local_preorder','public preorder')",
            DbValue.Uuid(product)), Ct);
        await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.brand_products(brand_id,product_id,price_usdt) VALUES ($1,$2,10)",
            DbValue.Uuid(fixture.Contexts[0].BrandId), DbValue.Uuid(product)), Ct);
        await seed.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.orders(id,user_id,product_id,quantity,order_kind,payment_status,payment_confirmed_at,currency,payment_method,
                unit_price_usdt,auto_price_toman_snapshot,pricing_rounding_step_snapshot,price_calculation_version,brand_public_name_snapshot,
                product_title_snapshot,payment_terms_snapshot,fulfillment_kind_snapshot,local_first_snapshot,is_preorder,fulfillment_status)
            VALUES ($1,$2,$3,1,'product_purchase','paid',CURRENT_TIMESTAMP,'USDT','crypto',10,false,5000,'public-v1',
                'public brand','public product','public terms','local',true,true,'waiting_stock')
            """, DbValue.Uuid(orderId), DbValue.Uuid(fixture.Users[0]), DbValue.Uuid(product)), Ct);
        await seed.CommitAsync(Ct);
        return orderId;
    }

    private static VerifiedTelegramUpdate Support(BrandFixture fixture, long updateId)
    {
        using var verifier = new TelegramWebhookVerifier(fixture.Contexts[0], Secret);
        return verifier.Verify([TelegramFixture.Header(Secret)], TelegramFixture.Body("/support", updateId: updateId, actorId: 7001, chatId: 7001, messageId: updateId));
    }
}
