using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using Platform.Core;
using Platform.Fulfillment;
using Platform.Persistence;
using Platform.Telegram;

namespace Platform.Testing;

public sealed record AcceptanceReport(int Version, string Scope, bool ReleaseReady, int Tables, int LastUnitBuyers, int SuccessfulLastUnitPurchases,
    int SettlementAttempts, int TopupPostings, int CompletedOrders, int ReleasedUnits, int SupplierSubmits, int SupplierLookups, bool RestartFencesVerified);
[JsonSerializable(typeof(AcceptanceReport))]
public sealed partial class AcceptanceJson : JsonSerializerContext;
public static class AcceptanceQualification
{
    public static async Task RunAsync(bool export = false, CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(users: 50, ct: ct);
        int tables = checked((int)await f.CountAsync("SELECT count(*) FROM pg_tables WHERE schemaname='shop'", ct));
        Require(tables == 28 && await f.CountAsync("SELECT count(*) FROM information_schema.columns WHERE table_schema='shop' AND data_type IN ('json','jsonb','ARRAY','real','double precision')", ct) == 0, "typed schema");
        var requests = Enumerable.Range(0, 50).Select(_ => f.Request()).ToArray();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var buyers = Enumerable.Range(0, 50).Select(async i =>
        { await start.Task; return await f.Store.PurchaseLocalAsync(f.Context(i), CheckoutFixture.Actor(i), requests[i], ct); }).ToArray();
        start.SetResult(); var results = await Task.WhenAll(buyers);
        int purchases = results.Count(r => r.Outcome == CheckoutOutcome.Purchased);
        Require(purchases == 1 && results.Count(r => r.Outcome == CheckoutOutcome.OutOfStock) == 49, "last unit race");
        int winner = Array.FindIndex(results, r => r.Outcome == CheckoutOutcome.Purchased);
        Guid local = requests[winner].OrderId;
        Require(await f.CountAsync("SELECT sum(balance)::bigint FROM shop.wallets", ct) == 4990, "one debit");
        await Anchor(f, winner, local, 901, ct);
        var oldPrepare = await Claim(f, JobKind.PrepareManifest, ct);
        await Expire(f, oldPrepare, ct);
        var prepare = await Claim(f, JobKind.PrepareManifest, ct);
        Require(prepare.Id == oldPrepare.Id && prepare.Attempt == oldPrepare.Attempt + 1, "durable prepare restart");
        var delivery = await new DeliveryStore(new(f.Database.Data), f.Secrets).PrepareAsync(f.Context(winner), prepare, ct);
        Require(delivery.HasValue, "persisted delivery");
        var oldSend = await Claim(f, JobKind.SendTelegram, ct); Guid generation;
        using (var dispatch = await new DeliveryStore(f.Sessions, f.Secrets).BeginAsync(f.Context(winner), oldSend, ct))
        { Require(dispatch is not null && dispatch.TargetMessageId == 901, "original edit target"); generation = dispatch!.Generation; }
        await Expire(f, oldSend, ct);
        var restarted = new DeliveryStore(new(f.Database.Data), f.Secrets);
        await restarted.RecoverExpiredAsync(f.Context(winner), delivery!.Value, ct);
        var recovered = await Claim(f, JobKind.SendTelegram, ct);
        Require(recovered.Id == oldSend.Id && recovered.Attempt == oldSend.Attempt + 1, "durable edit recovery");
        int sends = 0;
        await new LocalDeliveryWorker(restarted).RunAsync(f.Context(winner), recovered, (dispatch, _) =>
        {
            Require(dispatch.Generation == generation && dispatch.TargetMessageId == 901 && dispatch.BotId == f.Context(winner).BotId && dispatch.ChatId == CheckoutFixture.Actor(winner), "same private manifest target");
            sends++; return Task.FromResult(new DeliveryOutcome(DeliveryOutcomeKind.Succeeded, 901));
        }, ct);
        await restarted.RecordOutcomeAsync(f.Context(winner), recovered, new(DeliveryOutcomeKind.Succeeded, 901), ct);
        Require(sends == 1 && (await f.Store.PurchaseLocalAsync(f.Context(winner), CheckoutFixture.Actor(winner), requests[winner], ct)).Outcome == CheckoutOutcome.Replayed, "delivery and checkout replay");
        Require(await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", ct) == 1, "no restart debit");
        var payments = new PaymentSettlementFixture(f); await payments.EnableAsync(ct);
        Guid payment = Guid.NewGuid(); var evidence = new PaymentEvidenceStore(f.Sessions, f.Secrets);
        await evidence.CreateTopupAsync(f.Context(0), CheckoutFixture.Actor(0), payment, new(10.25m, Currency.Usd), CryptoAsset.UsdtBsc, ct);
        await using (var bind = await f.Sessions.BeginAsync(ct))
        { await evidence.BindInvoiceAsync(bind, f.Context(0), CheckoutFixture.Actor(0), payment, PaymentSettlementFixture.Provider(payment), 10.4m, ct); await bind.CommitAsync(ct); }
        using (var notice = PaymentNoticeFixture.Notice("topup:" + payment.ToString("D"), asset: "usdtbsc", provider: PaymentSettlementFixture.Provider(payment)))
            await evidence.CaptureAsync(f.Context(0), notice, (_, _) => Task.FromResult("protected:synthetic/acceptance"), ct);
        var options = new NpgsqlConnectionStringBuilder(f.Database.Data.ConnectionString) { MaxPoolSize = 16, IncludeErrorDetail = false };
        await using var bounded = new NpgsqlSlimDataSourceBuilder(options.ConnectionString).Build();
        var settled = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => new PaymentSettlementStore(new(bounded), f.Secrets, payments.Catalog).SettleAsync(f.Context(0), payment, PaymentSettlementFixture.Lookup, ct)));
        Require(settled.Count(r => r.Outcome == PaymentSettlementOutcome.Settled) == 1 && settled.Count(r => r.Outcome == PaymentSettlementOutcome.Replayed) == 99, "hundred settlement replays");
        int postings = checked((int)await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='wallet_topup' AND signed_amount=10.25", ct));
        Require(postings == 1 && await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='wallet_topup' AND user_id<>'" + f.Users[0].ToString("D") + "'", ct) == 0, "original topup member");
        await f.AddStockAsync(3, ct); await SupplierFixture.ConfigureAsync(f, ct);
        Guid mixed = await SupplierFixture.AcceptAsync(f, 5, ct);
        await Anchor(f, 0, mixed, 902, ct);
        var port = new SyntheticSupplier { Submit = _ => SyntheticSupplier.Unknown() };
        var store = new SupplierRequestStore(f.Sessions, f.Secrets);
        var submit = await Claim(f, JobKind.SubmitSupplier, ct);
        var original = await store.ReadAsync(f.Context(0), submit, ct);
        await new SupplierFulfillmentWorker(store, port, port).RunAsync(f.Context(0), submit, ct);
        Require(await f.CountAsync("SELECT count(*) FROM shop.fulfillments WHERE order_id='" + mixed.ToString("D") + "' AND status='released'", ct) == 0, "no partial supplier release");
        await using (var due = await f.Sessions.BeginAsync(ct))
        { await due.ExecuteAsync(new DbStatement("UPDATE shop.jobs SET available_at=clock_timestamp() WHERE job_kind='reconcile_supplier' AND status='pending'"), ct); await due.CommitAsync(ct); }
        await new SupplierFulfillmentWorker(new(new(f.Database.Data), f.Secrets), port, port).RunAsync(f.Context(0), await Claim(f, JobKind.ReconcileSupplier, ct), ct);
        Require(port.Submits == 1 && port.Lookups == 1 && port.Intents.All(i => i == original.Intent), "original supplier intent reconciliation");
        Require(await f.CountAsync("SELECT count(*) FROM shop.supplier_requests WHERE requested_quantity=2 AND status='succeeded'", ct) == 1, "mixed remainder");
        var finalStore = new DeliveryStore(new(f.Database.Data), f.Secrets);
        Require(await finalStore.PrepareAsync(f.Context(0), await Claim(f, JobKind.PrepareManifest, ct), ct) is not null, "complete mixed manifest");
        await new LocalDeliveryWorker(finalStore).RunAsync(f.Context(0), await Claim(f, JobKind.SendTelegram, ct), (dispatch, _) =>
        { Require(dispatch.ChatId == CheckoutFixture.Actor(0) && dispatch.BotId == f.Context(0).BotId && dispatch.TargetMessageId == 902, "mixed private route"); return Task.FromResult(new DeliveryOutcome(DeliveryOutcomeKind.Succeeded, 902)); }, ct);
        int completed = checked((int)await f.CountAsync("SELECT count(*) FROM shop.orders WHERE fulfillment_status='completed'", ct));
        int released = checked((int)await f.CountAsync("SELECT count(*) FROM shop.fulfillments WHERE status='released'", ct));
        Require(completed == 2 && released == 6 && await f.CountAsync("SELECT sum(balance*100)::bigint FROM shop.wallets", ct) == 495025, "combined final balances and complete delivery");
        Require(await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", ct) == 2 && await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind IN ('immediate_crypto_shortage_credit','late_crypto_credit','supplier_wallet_credit')", ct) == 0, "no invented compensation");
        int foreign = winner % 3 == 1 ? 2 : 1; bool denied = false;
        try { byte[] content = await finalStore.ReadManifestAsync(f.Context(foreign), CheckoutFixture.Actor(foreign), local, ct); CryptographicOperations.ZeroMemory(content); }
        catch (UnauthorizedAccessException) { denied = true; }
        Require(denied, "foreign manifest denial");
        if (export)
        {
            Directory.CreateDirectory("artifacts/acceptance");
            await File.WriteAllBytesAsync("artifacts/acceptance/synthetic-commerce.json", JsonSerializer.SerializeToUtf8Bytes(new AcceptanceReport(1, "synthetic-commerce-only", false, tables, buyers.Length, purchases, settled.Length, postings, completed, released, port.Submits, port.Lookups, true), AcceptanceJson.Default.AcceptanceReport), ct);
        }
    }
    private static async Task<JobLease> Claim(CheckoutFixture f, JobKind kind, CancellationToken ct)
    { await using var session = await new DbSessionFactory(f.Database.Data).BeginAsync(ct); var lease = await OutboxStore.ClaimAsync(session, kind, Guid.NewGuid(), TimeSpan.FromMinutes(1), ct); await session.CommitAsync(ct); return lease ?? throw new InvalidOperationException("Acceptance durable work missing."); }
    private static async Task Expire(CheckoutFixture f, JobLease lease, CancellationToken ct)
    { await using var session = await f.Sessions.BeginAsync(ct); await session.ExecuteAsync(new DbStatement("UPDATE shop.jobs SET lease_until=clock_timestamp()-INTERVAL '1 second' WHERE id=$1", DbValue.Uuid(lease.Id)), ct); await session.CommitAsync(ct); }
    private static async Task Anchor(CheckoutFixture f, int user, Guid order, long message, CancellationToken ct)
    {
        await using var session = await f.Sessions.BeginAsync(ct);
        await session.ExecuteAsync(new DbStatement("UPDATE shop.users SET flow_state='payment',flow_order_id=$1,flow_product_id=$2,flow_anchor_message_id=$3,flow_expires_at=clock_timestamp()+INTERVAL '1 hour',flow_revision=flow_revision+1 WHERE id=$4", DbValue.Uuid(order), DbValue.Uuid(f.Product), DbValue.Bigint(message), DbValue.Uuid(f.Users[user])), ct);
        byte[] secret = SHA256.HashData("public synthetic acceptance ingress"u8);
        using var verifier = new TelegramWebhookVerifier(f.Context(user), secret);
        await OrderDeliveryRouter.RecordAnchorAsync(session, verifier.Verify([TelegramFixture.Header(secret)], TelegramFixture.Body("order", callback: true, updateId: message, actorId: CheckoutFixture.Actor(user), chatId: CheckoutFixture.Actor(user), botId: f.Context(user).BotId, messageId: message)), order, ct);
        await session.CommitAsync(ct);
    }
    private static void Require(bool condition, string stage)
    { if (!condition) throw new InvalidOperationException("Synthetic acceptance failed: " + stage + "."); }
}
