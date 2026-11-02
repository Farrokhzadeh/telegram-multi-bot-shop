using Platform.Core;
using Platform.Payments;
using Platform.Fulfillment;
using Platform.Persistence;
using Platform.Telegram;

namespace Platform.Testing;

public static class PaymentMessageQualification
{
    public static async Task ProjectionAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, stock: 3, ct: ct);
        var payments = new PaymentSettlementFixture(f); await payments.EnableAsync(ct);
        var old = await payments.OrderAsync(ct: ct); var store = new PaymentInstructionStore(f.Sessions);
        var first = await Claim(store, ct); int calls = 0;
        var worker = new PaymentMessageWorker(store, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100));
        await worker.RunAsync(f.Context(0), first, (message, _) =>
        { Require(message.TargetMessageId is null && message.ChatId == CheckoutFixture.Actor(0) && message.BotId == f.Context(0).BotId && message.Text.Contains("Payment valid for 30 minutes", StringComparison.Ordinal), "private original instruction"); calls++; return Task.FromResult(new PaymentMessageOutcome(PaymentMessageOutcomeKind.Sent, 501)); }, ct);
        await worker.RunAsync(f.Context(0), first, (_, _) => throw new InvalidOperationException("Known success resent."), ct);
        Require(calls == 1, "known original success");
        var next = await payments.OrderAsync(ct: ct); await DueAsync(f, ct);
        var replacement = await Claim(store, ct);
        Require(await store.BeginAsync(f.Context(0), replacement, TimeSpan.FromMilliseconds(100), ct), "replacement work");
        var cancelled = await store.ReadForSendAsync(f.Context(0), replacement, ct);
        Require(cancelled is not null && cancelled.Order == old.Order && cancelled.TargetMessageId == 501 && cancelled.Text.Contains("Cancelled — replaced", StringComparison.Ordinal), "cancelled is a separate original-order edit");
        await store.RecordAsync(f.Context(0), replacement, new(PaymentMessageOutcomeKind.Sent, 501), ct);
        await DueAsync(f, ct); var pending = await Claim(store, ct);
        Require(await store.BeginAsync(f.Context(0), pending, TimeSpan.FromMilliseconds(100), ct), "new original instruction");
        await using (var early = await f.Sessions.BeginAsync(ct))
        { await early.ExecuteAsync(new DbStatement("UPDATE shop.jobs SET available_at=clock_timestamp() WHERE job_kind='expire_order_payment' AND order_id=$1", DbValue.Uuid(next.Order)), ct); await early.CommitAsync(ct); }
        JobLease expiry;
        await using (var claim = await f.Sessions.BeginAsync(ct))
        { expiry = await OutboxStore.ClaimAsync(claim, JobKind.ExpireOrderPayment, Guid.NewGuid(), TimeSpan.FromMinutes(1), ct) ?? throw new InvalidOperationException(); await claim.CommitAsync(ct); }
        await payments.CaptureAsync(next.Payment, next.Origin, next.Due, ct: ct);
        await payments.Store.SettleAsync(f.Context(0), next.Payment, PaymentSettlementFixture.Lookup, ct);
        Require(await new PaymentExpiryService(f.Sessions, f.Secrets).RunAsync(f.Context(0), expiry, ct) == PaymentExpiryOutcome.Terminal, "stale expiry cannot expire paid money");
        var fresh = await store.ReadForSendAsync(f.Context(0), pending, ct);
        Require(fresh is not null && fresh.Order == next.Order && fresh.Text.Contains("Payment accepted", StringComparison.Ordinal) && !fresh.Text.Contains("Expired", StringComparison.Ordinal), "authoritative reread before dispatch");
        await store.RecordAsync(f.Context(0), pending, new(PaymentMessageOutcomeKind.Sent, 502), ct);
        Require(await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='paid'", ct) == 1 && await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='cancelled' AND resolution_kind='replaced'", ct) == 1 && await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='expired'", ct) == 0, "no stale edit financial transition");
        Require(await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE last_error_code='latest_projection' AND status='cancelled'", ct) == 1, "superseded unsent status coalesced");
        bool foreign = false;
        try { await store.ReadForSendAsync(f.Context(1), pending, ct); } catch (UnauthorizedAccessException) { foreign = true; }
        Require(foreign, "foreign status work rejected");
    }
    public static async Task RecoveryAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, ct: ct);
        var payments = new PaymentSettlementFixture(f); await payments.EnableAsync(ct);
        await payments.OrderAsync(ct: ct); var store = new PaymentInstructionStore(f.Sessions);
        var first = await Claim(store, ct);
        Require(await store.BeginAsync(f.Context(0), first, TimeSpan.FromMilliseconds(100), ct), "first metadata attempt");
        Require(await store.ReadForSendAsync(f.Context(0), first, ct) is not null, "current instruction");
        await store.RecordAsync(f.Context(0), first, new(PaymentMessageOutcomeKind.RateLimited, RetryAfterSeconds: 7), ct);
        Require(await store.ClaimAsync(Guid.NewGuid(), ct) is null, "rate limit retained");
        await DueAsync(f, ct); var retry = await Claim(store, ct);
        Require(retry.Id == first.Id && retry.Attempt == first.Attempt + 1, "same rate-limited intent");
        Require(await store.BeginAsync(f.Context(0), retry, TimeSpan.FromMilliseconds(100), ct), "retry attempt");
        bool stale = false;
        try { await store.RecordAsync(f.Context(0), first, new(PaymentMessageOutcomeKind.Sent, 601), ct); } catch (InvalidOperationException) { stale = true; }
        Require(stale, "stale receipt fenced");
        await store.RecordAsync(f.Context(0), retry, new(PaymentMessageOutcomeKind.Sent, 601), ct);
        await payments.OrderAsync(ct: ct); await DueAsync(f, ct); var edit = await Claim(store, ct);
        Require(await store.BeginAsync(f.Context(0), edit, TimeSpan.FromMilliseconds(100), ct), "saved edit attempt");
        Require((await store.ReadForSendAsync(f.Context(0), edit, ct))?.TargetMessageId == 601, "saved edit target");
        await store.RecordAsync(f.Context(0), edit, new(PaymentMessageOutcomeKind.Uneditable), ct);
        Guid command = Guid.NewGuid(); var admin = await f.AdminAsync(ct);
        Guid fallback = await store.AuthorizeFallbackAsync(f.Context(0), admin, edit.Intent.OriginId!.Value, command, ct);
        Require(fallback == await store.AuthorizeFallbackAsync(f.Context(0), admin, edit.Intent.OriginId!.Value, command, ct), "audited fallback replay");
        await store.RecordAsync(f.Context(0), edit, new(PaymentMessageOutcomeKind.Sent, 601), ct);
        await DueAsync(f, ct);
        var worker = new PaymentMessageWorker(new(f.Sessions), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100)); int recoveryCalls = 0;
        for (int i = 0; i < 2; i++)
        {
            var work = await store.ClaimAsync(Guid.NewGuid(), ct); if (work is null) break;
            await worker.RunAsync(f.Context(0), work, (_, _) => { recoveryCalls++; return Task.FromResult(new PaymentMessageOutcome(PaymentMessageOutcomeKind.Sent, 602)); }, ct);
            await DueAsync(f, ct);
        }
        Require(await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE dedupe_key LIKE 'instruction-recovery:%' AND status='cancelled' AND last_error_code='known_success'", ct) == 1, "late known edit suppresses fallback");
        Require(recoveryCalls == 1 && await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", ct) == 0, "only new order metadata sent without money effects");
    }
    public static async Task TimeoutAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, ct: ct);
        var payments = new PaymentSettlementFixture(f); await payments.EnableAsync(ct); await payments.OrderAsync(ct: ct);
        var store = new PaymentInstructionStore(f.Sessions); var lease = await Claim(store, ct);
        var late = new TaskCompletionSource<PaymentMessageOutcome>(TaskCreationOptions.RunContinuationsAsynchronously); int calls = 0;
        await new PaymentMessageWorker(store, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100)).RunAsync(f.Context(0), lease, (_, _) => { Interlocked.Increment(ref calls); return late.Task; }, ct);
        Require(await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE status='uncertain'", ct) == 1 && await store.ClaimAsync(Guid.NewGuid(), ct) is null, "unknown new send pauses automatic retry");
        late.SetResult(new(PaymentMessageOutcomeKind.Sent, 701));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE status='succeeded' AND telegram_message_id=701", deadline.Token) != 1) await Task.Delay(10, deadline.Token);
        Require(calls == 1, "late known receipt retained without another send");
    }
    public static async Task LeaseRecoveryAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, ct: ct);
        var payments = new PaymentSettlementFixture(f); await payments.EnableAsync(ct); await payments.OrderAsync(ct: ct);
        var store = new PaymentInstructionStore(f.Sessions); var send = await Claim(store, ct);
        Require(await store.BeginAsync(f.Context(0), send, TimeSpan.FromMilliseconds(100), ct) && await store.ReadForSendAsync(f.Context(0), send, ct) is not null, "started new send");
        await ExpireAsync(f, send, ct); await new PaymentInstructionStore(new(f.Database.Data)).RecoverExpiredAsync(f.Context(0), send.Intent.OriginId!.Value, ct);
        Require(await store.ClaimAsync(Guid.NewGuid(), ct) is null && await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE status='uncertain'", ct) == 1, "crashed new send cannot automatically replay");
        await store.RecordAsync(f.Context(0), send, new(PaymentMessageOutcomeKind.Sent, 801), ct);
        await payments.OrderAsync(ct: ct); await DueAsync(f, ct); var edit = await Claim(store, ct);
        Require(await store.BeginAsync(f.Context(0), edit, TimeSpan.FromMilliseconds(100), ct), "started saved edit");
        Require((await store.ReadForSendAsync(f.Context(0), edit, ct))?.TargetMessageId == 801, "persisted original target");
        await ExpireAsync(f, edit, ct); await new PaymentInstructionStore(new(f.Database.Data)).RecoverExpiredAsync(f.Context(0), edit.Intent.OriginId!.Value, ct);
        bool oldOwner = false;
        try { await store.BeginAsync(f.Context(0), edit, TimeSpan.FromMilliseconds(100), ct); } catch (DbOperationException e) when (e.Failure.Kind == DbFailureKind.LeaseLost) { oldOwner = true; }
        Require(oldOwner, "expired edit owner fenced"); await DueAsync(f, ct);
        bool recovered = false;
        for (int i = 0; i < 2; i++)
        {
            var current = await Claim(store, ct);
            Require(await store.BeginAsync(f.Context(0), current, TimeSpan.FromMilliseconds(100), ct), "reconstructed metadata worker");
            var dispatch = await store.ReadForSendAsync(f.Context(0), current, ct) ?? throw new InvalidOperationException();
            if (current.Id == edit.Id)
            {
                Require(current.Attempt == edit.Attempt + 1 && dispatch.TargetMessageId == 801 && dispatch.Text.Contains("Cancelled — replaced", StringComparison.Ordinal), "same edit fresh state and attempt");
                bool wrongTarget = false;
                try { await store.RecordAsync(f.Context(0), current, new(PaymentMessageOutcomeKind.Sent, 999), ct); } catch (InvalidOperationException) { wrongTarget = true; }
                Require(wrongTarget, "wrong remote target cannot complete work");
                bool stale = false;
                try { await store.RecordAsync(f.Context(0), edit, new(PaymentMessageOutcomeKind.Sent, 801), ct); } catch (InvalidOperationException) { stale = true; }
                Require(stale, "old edit receipt cannot complete new attempt"); recovered = true;
            }
            await store.RecordAsync(f.Context(0), current, new(PaymentMessageOutcomeKind.Sent, dispatch.TargetMessageId ?? 802), ct); await DueAsync(f, ct);
        }
        Require(recovered && await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", ct) == 0, "restart preserves metadata and money");
    }
    public static async Task CredentialOwnershipAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, ct: ct);
        var payments = new PaymentSettlementFixture(f); await payments.EnableAsync(ct); var order = await payments.OrderAsync(ct: ct);
        var messages = new PaymentInstructionStore(f.Sessions); var initial = await Claim(messages, ct);
        await new PaymentMessageWorker(messages, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100)).RunAsync(f.Context(0), initial, (_, _) => Task.FromResult(new PaymentMessageOutcome(PaymentMessageOutcomeKind.Sent, 901)), ct);
        await payments.CaptureAsync(order.Payment, order.Origin, order.Due, ct: ct); await payments.Store.SettleAsync(f.Context(0), order.Payment, PaymentSettlementFixture.Lookup, ct);
        await DueAsync(f, ct); var metadata = await Claim(messages, ct);
        Require(await messages.BeginAsync(f.Context(0), metadata, TimeSpan.FromMilliseconds(100), ct), "metadata started before manifest");
        Require((await messages.ReadForSendAsync(f.Context(0), metadata, ct))?.TargetMessageId == 901, "metadata owns original saved target");
        var deliveryStore = new DeliveryStore(f.Sessions, f.Secrets);
        Require(await deliveryStore.PrepareAsync(f.Context(0), await CredentialClaim(f, JobKind.PrepareManifest, ct), ct) is not null, "complete manifest prepared");
        var credential = await CredentialClaim(f, JobKind.SendTelegram, ct); int calls = 0;
        await new LocalDeliveryWorker(deliveryStore).RunAsync(f.Context(0), credential, (_, _) => { calls++; return Task.FromResult(new DeliveryOutcome(DeliveryOutcomeKind.Succeeded, 901)); }, ct);
        Require(calls == 0, "credential edit waits for active metadata transport");
        await messages.RecordAsync(f.Context(0), metadata, new(PaymentMessageOutcomeKind.Sent, 901), ct); await DueAsync(f, ct);
        var current = await CredentialClaim(f, JobKind.SendTelegram, ct);
        await new LocalDeliveryWorker(deliveryStore).RunAsync(f.Context(0), current, (dispatch, _) => { Require(dispatch.TargetMessageId == 901 && dispatch.Content.Length > 0, "same protected complete manifest target"); calls++; return Task.FromResult(new DeliveryOutcome(DeliveryOutcomeKind.Succeeded, 901)); }, ct);
        Require(calls == 1 && await f.CountAsync("SELECT count(*) FROM shop.orders WHERE fulfillment_status='completed'", ct) == 1, "credential result completed after metadata");
        await using (var enqueue = await f.Sessions.BeginAsync(ct))
        {
            Guid id = Guid.NewGuid();
            await enqueue.ExecuteAsync(new DbStatement("INSERT INTO shop.telegram_deliveries(id,brand_id,recipient_kind,user_id,purpose,action,target_message_id,dedupe_key,order_id) VALUES ($1,$2,'customer_dm',$3,'order_status','edit',901,$4,$5)", DbValue.Uuid(id), DbValue.Uuid(f.Brands[0]), DbValue.Uuid(f.Users[0]), DbValue.Text("public-stale-status:" + order.Order.ToString("D")), DbValue.Uuid(order.Order)), ct);
            await OutboxStore.EnqueueAsync(enqueue, JobIntent.Telegram(f.Brands[0], id), id, DateTime.UtcNow, ct); await enqueue.CommitAsync(ct);
        }
        await DueAsync(f, ct); var stale = await Claim(messages, ct); int staleCalls = 0;
        await new PaymentMessageWorker(messages, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100)).RunAsync(f.Context(0), stale, (_, _) => { staleCalls++; return Task.FromResult(new PaymentMessageOutcome(PaymentMessageOutcomeKind.Sent, 901)); }, ct);
        Require(staleCalls == 0 && await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE status='cancelled' AND last_error_code='credential_delivery_active'", ct) == 1, "late status cannot erase credential result");
        Require(await f.CountAsync("SELECT count(*) FROM shop.fulfillments WHERE status='released'", ct) == 1 && await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", ct) == 0, "credential ownership fence never repeats money or allocation");
    }
    private static async Task<JobLease> CredentialClaim(CheckoutFixture f, JobKind kind, CancellationToken ct)
    { await using var session = await f.Sessions.BeginAsync(ct); var lease = await OutboxStore.ClaimAsync(session, kind, Guid.NewGuid(), TimeSpan.FromMinutes(1), ct); await session.CommitAsync(ct); return lease ?? throw new InvalidOperationException("Credential ownership fixture work missing."); }
    private static async Task ExpireAsync(CheckoutFixture f, JobLease lease, CancellationToken ct)
    { await using var session = await f.Sessions.BeginAsync(ct); await session.ExecuteAsync(new DbStatement("UPDATE shop.jobs SET lease_until=clock_timestamp()-INTERVAL '1 second' WHERE id=$1", DbValue.Uuid(lease.Id)), ct); await session.CommitAsync(ct); }
    private static async Task<JobLease> Claim(PaymentInstructionStore store, CancellationToken ct) => await store.ClaimAsync(Guid.NewGuid(), ct) ?? throw new InvalidOperationException("Payment message fixture work missing.");
    private static async Task DueAsync(CheckoutFixture f, CancellationToken ct)
    {
        await using var session = await f.Sessions.BeginAsync(ct);
        await session.ExecuteAsync(new DbStatement("UPDATE shop.telegram_deliveries SET last_attempt_at=clock_timestamp()-INTERVAL '10 seconds',next_attempt_at=clock_timestamp()"), ct);
        await session.ExecuteAsync(new DbStatement("UPDATE shop.jobs SET available_at=clock_timestamp() WHERE job_kind='send_telegram' AND status='retry'"), ct);
        await session.CommitAsync(ct);
    }
    private static void Require(bool condition, string stage) { if (!condition) throw new InvalidOperationException("Payment message qualification failed: " + stage + "."); }
}
