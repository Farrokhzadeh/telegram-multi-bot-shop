using Platform.Core;
using Platform.Payments;
using Platform.Persistence;

namespace Platform.Testing;

public static class PaymentActivityQualification
{
    public static async Task RetentionAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, ct: ct);
        await new PaymentSettlementFixture(f).EnableAsync(ct);
        Guid payment = Guid.NewGuid();
        await using (var seed = await f.Sessions.BeginAsync(ct))
        {
            await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.payments(id,topup_user_id,payment_method,quote_currency,amount_due,idempotency_ref,network_asset,created_at) VALUES($1,$2,'crypto','USD',1,$3,'USDTTRC20',CURRENT_TIMESTAMP-INTERVAL '181 days')", DbValue.Uuid(payment), DbValue.Uuid(f.Users[0]), DbValue.Text("topup:" + payment.ToString("D"))), ct);
            await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.bot_activity_events(id,user_id,event_type,source_kind,action_code,outcome_code,idempotency_key,occurred_at) SELECT gen_random_uuid(),topup_user_id,'payment','system','topup_created','accepted','workflow:topup_created:'||replace(id::text,'-',''),created_at FROM shop.payments WHERE id=$1", DbValue.Uuid(payment)), ct);
            await seed.CommitAsync(ct);
        }
        bool foreign = false;
        await using (var invalid = await f.Sessions.BeginAsync(ct))
        {
            try { await invalid.ExecuteAsync(new DbStatement("INSERT INTO shop.bot_activity_events(id,user_id,event_type,source_kind,action_code,outcome_code,idempotency_key,occurred_at) SELECT gen_random_uuid(),$2,'payment','system','topup_created','accepted','workflow:topup_created:'||replace(id::text,'-',''),created_at FROM shop.payments WHERE id=$1", DbValue.Uuid(payment), DbValue.Uuid(f.Users[1])), ct); }
            catch (DbOperationException e) when (e.Failure.Kind == DbFailureKind.Constraint) { foreign = true; }
        }
        string before = await WorkflowActivityQualification.DomainAsync(f.Sessions, true, ct);
        var result = await new BotActivityRetentionStore(f.Sessions).CleanAsync(ct: ct);
        Require(foreign && result.Deleted == 1 && result.Backlog.Expired == 0, "only exact payment membership provenance authorizes history retirement");
        var evidence = new PaymentEvidenceStore(f.Sessions);
        for (int i = 0; i < 100; i++) await evidence.CreateTopupAsync(f.Context(0), CheckoutFixture.Actor(0), payment, new(1, Currency.Usd), CryptoAsset.UsdtTrc20, ct);
        Require(await f.CountAsync("SELECT count(*) FROM shop.bot_activity_events", ct) == 0 && before == await WorkflowActivityQualification.DomainAsync(f.Sessions, true, ct), "payment replay after expiry cannot retimestamp history or mutate domain records");
    }
    public static async Task TopupAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, ct: ct);
        var fixture = new PaymentSettlementFixture(f); await fixture.EnableAsync(ct);
        var evidence = new PaymentEvidenceStore(f.Sessions);
        for (int user = 0; user < 3; user++)
        {
            Guid payment = Guid.NewGuid();
            await evidence.CreateTopupAsync(f.Context(user), CheckoutFixture.Actor(user), payment, new(10, Currency.Usd), CryptoAsset.UsdtTrc20, ct);
            await using (var bind = await f.Sessions.BeginAsync(ct))
            { await evidence.BindInvoiceAsync(bind, f.Context(user), CheckoutFixture.Actor(user), payment, PaymentSettlementFixture.Provider(payment), 10.4m, ct); await bind.CommitAsync(ct); }
            using (var notice = PaymentNoticeFixture.Notice("topup:" + payment.ToString("D"), status: "confirming", actual: 5, quote: 10, provider: PaymentSettlementFixture.Provider(payment)))
                await evidence.CaptureAsync(f.Context(user), notice, (e, _) => Task.FromResult("protected:synthetic/" + e.Digest), ct);
            Require(await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='wallet_topup'", ct) == user, "observed evidence is not settlement");
            await fixture.CaptureAsync(payment, "topup:" + payment.ToString("D"), 10, user, ct);
            Require((await fixture.Store.SettleAsync(f.Context(user), payment, PaymentSettlementFixture.Lookup, ct)).Outcome == PaymentSettlementOutcome.Settled, "verified topup settlement");
            for (int i = 0; i < 100; i++)
            {
                await evidence.CreateTopupAsync(f.Context(user), CheckoutFixture.Actor(user), payment, new(10, Currency.Usd), CryptoAsset.UsdtTrc20, ct);
                Require((await fixture.Store.SettleAsync(f.Context(user), payment, (_, _) => throw new InvalidOperationException(), ct)).Outcome == PaymentSettlementOutcome.Replayed, "topup replay is independent of analytics");
            }
        }
        foreach (string action in new[] { "topup_created", "payment_awaiting", "payment_observed", "payment_evidence_ready", "payment_settled" })
            Require(await f.CountAsync("SELECT count(*) FROM shop.bot_activity_events WHERE action_code='" + action + "' AND product_id IS NULL AND order_id IS NULL AND quantity_selected IS NULL", ct) == 3, "one safe context-free topup state per brand");
        Require(await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='wallet_topup' AND signed_amount=10", ct) == 3 && await f.CountAsync("SELECT count(*) FROM shop.orders", ct) == 0, "exact original USD credits without fictional orders");
    }
    public static async Task ReviewAndBankAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, stock: 3, ct: ct);
        var fixture = new PaymentSettlementFixture(f); await fixture.EnableAsync(ct);
        var crypto = await fixture.OrderAsync(user: 1, ct: ct);
        var evidence = new PaymentEvidenceStore(f.Sessions);
        using (var notice = PaymentNoticeFixture.Notice(crypto.Origin, asset: "usdtbsc", quote: crypto.Due, provider: PaymentSettlementFixture.Provider(crypto.Payment)))
            for (int i = 0; i < 100; i++) await evidence.CaptureAsync(f.Context(1), notice, (e, _) => Task.FromResult("protected:synthetic/" + e.Digest), ct);
        Require(await f.CountAsync("SELECT count(*) FROM shop.bot_activity_events WHERE action_code='payment_review' AND outcome_code='blocked'", ct) == 1 && await f.CountAsync("SELECT count(*) FROM shop.payments WHERE status='settled'", ct) == 0, "review is one real unresolved transition without acceptance");
        await fixture.EnableBankAsync(ct);
        var bank = await fixture.BankOrderAsync(ct); var admin = await f.AdminAsync(ct);
        await fixture.Manual.SubmitAsync(f.Context(0), CheckoutFixture.Actor(0), bank.Payment, "protected:synthetic/bank-activity", ct);
        long revision;
        await using (var read = await f.Sessions.BeginAsync(ct)) revision = await read.QuerySingleAsync(new DbStatement("SELECT revision FROM shop.orders WHERE id=$1", DbValue.Uuid(bank.Order)), r => r.GetInt64(0), ct);
        Guid command = Guid.NewGuid();
        await fixture.Manual.DecideAsync(f.Context(0), admin, bank.Payment, false, revision, command, "public denial", ct);
        for (int i = 0; i < 100; i++) Require(await fixture.Manual.DecideAsync(f.Context(0), admin, bank.Payment, false, revision, command, "public denial", ct) == ManualDepositOutcome.Replayed, "audited denial replay");
        Require(await f.CountAsync("SELECT count(*) FROM shop.bot_activity_events e JOIN shop.audit_events a ON e.idempotency_key='workflow:payment_denied:'||replace(a.id::text,'-','') WHERE e.occurred_at=a.created_at AND e.outcome_code='rejected'", ct) == 1, "denial retains immutable audit identity and original clock");
        await fixture.Manual.DecideAsync(f.Context(0), admin, bank.Payment, true, revision, Guid.NewGuid(), "public approval", ct);
        Require(await f.CountAsync("SELECT count(*) FROM shop.bot_activity_events WHERE action_code='payment_denied'", ct) == 1 && await f.CountAsync("SELECT count(*) FROM shop.bot_activity_events WHERE action_code='payment_settled'", ct) == 1, "later approval preserves the actual historical denial");
    }
    public static async Task RollbackAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, ct: ct);
        var fixture = new PaymentSettlementFixture(f); await fixture.EnableAsync(ct);
        Guid payment = Guid.NewGuid(); var evidence = new PaymentEvidenceStore(f.Sessions);
        await evidence.CreateTopupAsync(f.Context(0), CheckoutFixture.Actor(0), payment, new(10, Currency.Usd), CryptoAsset.UsdtTrc20, ct);
        await using (var bind = await f.Sessions.BeginAsync(ct))
        { await evidence.BindInvoiceAsync(bind, f.Context(0), CheckoutFixture.Actor(0), payment, PaymentSettlementFixture.Provider(payment), 10.4m, ct); await bind.CommitAsync(ct); }
        await fixture.CaptureAsync(payment, "topup:" + payment.ToString("D"), 10, ct: ct);
        await using (var fail = f.Database.Data.CreateCommand("CREATE FUNCTION shop.synthetic_payment_activity_failure() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION USING ERRCODE='23514',MESSAGE='synthetic activity failure'; END $$; CREATE CONSTRAINT TRIGGER synthetic_payment_activity_failure AFTER INSERT ON shop.bot_activity_events DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (NEW.action_code='payment_settled') EXECUTE FUNCTION shop.synthetic_payment_activity_failure()")) await fail.ExecuteNonQueryAsync(ct);
        bool failed = false;
        try { await fixture.Store.SettleAsync(f.Context(0), payment, PaymentSettlementFixture.Lookup, ct); } catch (DbOperationException e) when (e.Failure.Kind == DbFailureKind.Constraint) { failed = true; }
        Require(failed && await f.CountAsync("SELECT count(*) FROM shop.payments WHERE status='settled'", ct) == 0 && await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", ct) == 0 && await f.CountAsync("SELECT count(*) FROM shop.bot_activity_events WHERE action_code='payment_settled'", ct) == 0, "deferred activity failure rolls back settlement and credit together");
        await using (var repair = f.Database.Data.CreateCommand("DROP TRIGGER synthetic_payment_activity_failure ON shop.bot_activity_events; DROP FUNCTION shop.synthetic_payment_activity_failure()")) await repair.ExecuteNonQueryAsync(ct);
        Require((await fixture.Store.SettleAsync(f.Context(0), payment, PaymentSettlementFixture.Lookup, ct)).Outcome == PaymentSettlementOutcome.Settled && await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='wallet_topup'", ct) == 1, "same original invoice retries once");
    }
    private static void Require(bool value, string stage) { if (!value) throw new InvalidOperationException("Payment activity qualification failed: " + stage + "."); }
}
