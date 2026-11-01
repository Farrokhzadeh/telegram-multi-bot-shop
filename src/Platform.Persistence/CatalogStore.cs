using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using NpgsqlTypes;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

public enum CatalogAvailability { Available, OutOfStock }
public enum SupplierCatalogStatus { Available, OutOfStock, Ineligible, Error }
public sealed record SupplierAccount(string Provider, string AccountRef);
public sealed record SupplierCatalogRequest(string Provider, string AccountRef, string ProductRef, string? VariantRef, int Quantity)
{
    public override string ToString() => "SupplierCatalogRequest [redacted]";
}
public sealed record SupplierCatalogResult(SupplierCatalogStatus Status, int Quantity, decimal? UnitCost = null, string? Currency = null, string? QuoteRef = null, bool ReadyItem = true)
{
    public override string ToString() => "SupplierCatalogResult [redacted]";
}
public sealed record CatalogQuery(Guid ProductId, int Quantity, CheckoutPayment Payment, CheckoutKind Kind = CheckoutKind.ProductPurchase);
public sealed record CatalogItem(Guid ProductId, ProductPresentation Presentation, decimal UnitUsdt, decimal? UnitToman, bool AutoToman, CatalogAvailability Availability, int Minimum, int Maximum, bool Wallet, bool Crypto, bool Bank)
{
    public bool CanPurchase => Availability == CatalogAvailability.Available && (Wallet || Crypto || Bank);
}
public sealed record CheckoutSupply(int LocalUnits, int RemoteUnits, bool WaitingForLocalStock, string Kind);
public sealed class PreparedCheckout
{
    internal PreparedCheckout(object owner, BrandContext context, long actor, Guid member, CatalogQuery query, CheckoutQuote quote, CheckoutSupply supply,
        long policyRevision, long productRevision, long listingRevision, Guid? source, long? sourceRevision, long? observationRevision)
    {
        Owner = owner; Context = context; Actor = actor; Member = member; Query = query; Quote = quote; Supply = supply;
        PolicyRevision = policyRevision; ProductRevision = productRevision; ListingRevision = listingRevision; Source = source; SourceRevision = sourceRevision; ObservationRevision = observationRevision;
    }
    internal object Owner { get; }
    internal BrandContext Context { get; }
    internal long Actor { get; }
    internal Guid Member { get; }
    internal CatalogQuery Query { get; }
    internal long PolicyRevision { get; }
    internal long ProductRevision { get; }
    internal long ListingRevision { get; }
    internal Guid? Source { get; }
    internal long? SourceRevision { get; }
    internal long? ObservationRevision { get; }
    internal long IssuedAt { get; } = Stopwatch.GetTimestamp();
    internal int Used;
    public CheckoutQuote Quote { get; }
    public CheckoutSupply Supply { get; }
    public override string ToString() => "PreparedCheckout [redacted]";
}

public sealed class CatalogStore
{
    private readonly DbSessionFactory sessions;
    private readonly SecretSnapshot secrets;
    private readonly Dictionary<string, string> accounts = new(StringComparer.Ordinal);
    private readonly Func<SupplierCatalogRequest, CancellationToken, Task<SupplierCatalogResult>> fetch;
    public CatalogStore(DbSessionFactory sessions, SecretSnapshot secrets, IEnumerable<SupplierAccount> accounts,
        Func<SupplierCatalogRequest, CancellationToken, Task<SupplierCatalogResult>> fetch)
    {
        this.sessions = sessions; this.secrets = secrets; this.fetch = fetch;
        foreach (SupplierAccount account in accounts)
            if (!Label(account.Provider) || !Label(account.AccountRef) || !this.accounts.TryAdd(account.Provider, account.AccountRef)) throw new ArgumentException("One trusted account per provider is required.");
    }

    public async Task<CatalogItem?> GetAsync(BrandContext context, long verifiedActorId, Guid productId, CancellationToken ct = default)
    {
        var state = await RefreshAsync(context, verifiedActorId, new(productId, 1, CheckoutPayment.Wallet), true, ct);
        if (state is null) return null;
        var display = CheckoutQuote.Create(new(state.UnitUsdt, state.UnitToman, state.AutoToman, state.FxEnabled, state.Fx, state.MaximumFxAge), state.Presentation, 1, CheckoutPayment.Wallet, [], state.Now);
        return new(productId, state.Presentation, state.UnitUsdt, display.Terms.UnitToman, state.AutoToman,
            Supply(state, state.Minimum) is null ? CatalogAvailability.OutOfStock : CatalogAvailability.Available, state.Minimum, state.Maximum, state.Wallet, state.Crypto, state.Bank && state.Market == "iran");
    }

    public async Task<CheckoutQuote> QuoteAsync(BrandContext context, long verifiedActorId, CatalogQuery query, CancellationToken ct = default) =>
        (await PrepareAsync(context, verifiedActorId, query, ct)).Quote;

    public async Task<PreparedCheckout> PrepareAsync(BrandContext context, long verifiedActorId, CatalogQuery query, CancellationToken ct = default)
    {
        ValidateQuery(query);
        var state = await RefreshAsync(context, verifiedActorId, query, false, ct);
        if (state is null || Supply(state, query.Quantity, query.Kind) is not CheckoutSupply supply) throw new PricingRejectedException();
        await using var session = await sessions.BeginAsync(ct);
        Data? current = await LoadAsync(session, context, verifiedActorId, query.ProductId, ct);
        if (current is null || !SameState(state, current) || Supply(current, query.Quantity, query.Kind) != supply) Reject(session);
        CheckoutQuote quote = await BuildQuoteAsync(session, context, current, query, ct);
        var prepared = new PreparedCheckout(this, context, verifiedActorId, current.Member, query, quote, supply, current.PolicyRevision, current.ProductRevision,
            current.ListingRevision, supply.RemoteUnits > 0 ? current.Source?.Id : null, supply.RemoteUnits > 0 ? current.Source?.Revision : null, supply.RemoteUnits > 0 ? current.Source?.ObservationRevision : null);
        await session.CommitAsync(ct);
        return prepared;
    }

    public async Task RequireCurrentAsync(DbSession session, BrandContext context, long verifiedActorId, PreparedCheckout prepared, CancellationToken ct = default)
    {
        try
        {
            if (!ReferenceEquals(prepared.Owner, this) || prepared.Context != context || prepared.Actor != verifiedActorId ||
                Interlocked.Exchange(ref prepared.Used, 1) != 0 || Stopwatch.GetElapsedTime(prepared.IssuedAt) > TimeSpan.FromSeconds(30)) Reject(session);
            Data? current = await LoadAsync(session, context, verifiedActorId, prepared.Query.ProductId, ct);
            if (current is null || current.Member != prepared.Member || current.PolicyRevision != prepared.PolicyRevision || current.ProductRevision != prepared.ProductRevision || current.ListingRevision != prepared.ListingRevision ||
                Supply(current, prepared.Query.Quantity, prepared.Query.Kind) != prepared.Supply || (prepared.Supply.RemoteUnits > 0 &&
                (current.Source?.Id != prepared.Source || current.Source?.Revision != prepared.SourceRevision || current.Source?.ObservationRevision != prepared.ObservationRevision))) Reject(session);
            CheckoutQuote quote = await BuildQuoteAsync(session, context, current, prepared.Query, ct);
            if (quote.Terms != prepared.Quote.Terms || quote.RequestedPayment != prepared.Quote.RequestedPayment || Stopwatch.GetElapsedTime(prepared.IssuedAt) > TimeSpan.FromSeconds(30)) Reject(session);
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }

    public async Task<RestockObservation> ObserveRestockAsync(BrandContext context, long verifiedActorId, Guid productId, CancellationToken ct = default)
    {
        Data? state = await RefreshAsync(context, verifiedActorId, new(productId, 1, CheckoutPayment.Wallet), true, ct);
        if (state is null) throw new PricingRejectedException();
        return new(productId, state.ProductRevision, state.ListingRevision, state.Now, Supply(state, state.Minimum) is null);
    }

    internal async Task BindSupplierAsync(DbSession session, BrandContext context, PreparedCheckout prepared, Guid order, CancellationToken ct)
    {
        Data? data = await LoadAsync(session, context, prepared.Actor, prepared.Query.ProductId, ct);
        if (data is null || Supply(data, prepared.Query.Quantity) != prepared.Supply || prepared.Supply.RemoteUnits <= 0 ||
            data.Source is not Source source || source.Id != prepared.Source || source.Revision != prepared.SourceRevision ||
            source.ObservationRevision != prepared.ObservationRevision || Stopwatch.GetElapsedTime(prepared.IssuedAt) > TimeSpan.FromSeconds(30)) Reject(session);
        Source selected = data.Source!;
        Guid id = Guid.CreateVersion7();
        string intent = "order:" + order.ToString("D");
        byte[] hash;
        using (var bytes = new MemoryStream())
        {
            using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write("supplier-binding-v1"); writer.Write(order.ToByteArray()); writer.Write(selected.Id.ToByteArray()); writer.Write(selected.Revision);
                writer.Write(selected.Provider); writer.Write(selected.Account!); writer.Write(selected.ProductRef); writer.Write(selected.VariantRef is not null);
                if (selected.VariantRef is string variant) writer.Write(variant);
                writer.Write(prepared.Supply.RemoteUnits); writer.Write(ExactDecimal.Format(selected.Cost!.Value)); writer.Write(selected.Currency!);
                writer.Write(selected.Quote!); writer.Write(selected.ObservedAt!.Value.Ticks);
            }
            hash = SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)));
        }
        await session.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.supplier_requests(id,order_id,requested_quantity,product_supplier_source_id,source_revision,provider_code,account_ref,
                external_product_id,external_variant_id,local_intent_key,request_sha256,status,cost_baseline_amount,cost_currency,cost_quote_ref,cost_observed_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,'pending',$12,$13,$14,$15)
            """, DbValue.Uuid(id), DbValue.Uuid(order), DbValue.Integer(prepared.Supply.RemoteUnits), DbValue.Uuid(selected.Id), DbValue.Bigint(selected.Revision),
            DbValue.Text(selected.Provider), DbValue.Text(selected.Account!), DbValue.Text(selected.ProductRef), Text(selected.VariantRef), DbValue.Text(intent), DbValue.Binary(hash),
            DbValue.Numeric(selected.Cost!.Value), DbValue.Text(selected.Currency!), DbValue.Text(selected.Quote!), DbValue.Utc(selected.ObservedAt!.Value)), ct);
        await OutboxStore.EnqueueAsync(session, JobIntent.Supplier(JobKind.SubmitSupplier, context.BrandId, id), order, DateTime.UtcNow, ct);
    }

    private async Task<Data?> RefreshAsync(BrandContext context, long actor, CatalogQuery query, bool display, CancellationToken ct)
    {
        Data? before;
        await using (var preflight = await sessions.BeginAsync(ct)) before = await LoadAsync(preflight, context, actor, query.ProductId, ct);
        if (before is null) return null;
        int quantity = display ? before.Minimum : query.Quantity;
        if (!display && (quantity < before.Minimum || quantity > before.Maximum || !PaymentAllowed(before, query.Payment))) throw new PricingRejectedException();
        int remote = query.Kind == CheckoutKind.PrepaidUnits ? 0 : RemoteNeeded(before, quantity);
        SupplierCatalogResult? result = null;
        string? account = null;
        if (remote > 0 && before.Source is { Enabled: true } source && accounts.TryGetValue(source.Provider, out account))
        {
            try { result = await fetch(new(source.Provider, account, source.ProductRef, source.VariantRef, remote), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { result = new(SupplierCatalogStatus.Error, 0); }
            result = Normalize(result);
        }
        await using var apply = await sessions.BeginAsync(ct);
        Data? current = await LoadAsync(apply, context, actor, query.ProductId, ct);
        if (current is null) return null;
        if (!SameState(before, current) || (query.Kind == CheckoutKind.PrepaidUnits ? 0 : RemoteNeeded(current, quantity)) != remote) Reject(apply);
        if (result is not null && account is not null)
        {
            Source observedSource = current.Source!;
            string status = result.Status switch { SupplierCatalogStatus.Available => "available", SupplierCatalogStatus.OutOfStock => "out_of_stock", SupplierCatalogStatus.Ineligible => "ineligible", _ => "error" };
            int changed = await apply.ExecuteAsync(new DbStatement("""
                UPDATE shop.product_supplier_sources SET observation_revision=observation_revision+1,observed_source_revision=revision,observed_account_ref=$1,
                    observed_at=clock_timestamp(),availability_code=$2,available_quantity=$3,observed_unit_cost=$4,observed_currency=$5,quote_ref=$6,
                    eligibility_code=$7,observation_error_code=$8,updated_at=clock_timestamp()
                WHERE id=$9 AND revision=$10 AND observation_revision=$11 AND enabled
                """, DbValue.Text(account), DbValue.Text(status), DbValue.Integer(result.Quantity), Numeric(result.UnitCost), Text(result.Currency), Text(result.QuoteRef),
                DbValue.Text(result.Status == SupplierCatalogStatus.Available || result.Status == SupplierCatalogStatus.OutOfStock ? "ready_item" : result.Status == SupplierCatalogStatus.Ineligible ? "ineligible" : "error"),
                result.Status == SupplierCatalogStatus.Error ? DbValue.Text("source_unavailable") : DbValue.Null(NpgsqlDbType.Text), DbValue.Uuid(observedSource.Id), DbValue.Bigint(observedSource.Revision), DbValue.Bigint(observedSource.ObservationRevision)), ct);
            if (changed != 1) Reject(apply);
            current = await LoadAsync(apply, context, actor, query.ProductId, ct) ?? throw new PricingRejectedException();
        }
        if (Supply(current, current.Minimum) is not null) await WakeRestockAsync(apply, current.ProductId, ct);
        await apply.CommitAsync(ct);
        return current;
    }

    private async Task<CheckoutQuote> BuildQuoteAsync(DbSession session, BrandContext context, Data data, CatalogQuery query, CancellationToken ct)
    {
        if (query.Quantity < data.Minimum || query.Quantity > data.Maximum || !PaymentAllowed(data, query.Payment)) throw new PricingRejectedException();
        var rows = await session.QueryManyAsync(new DbStatement("""
            SELECT d.id,r.id,d.code_ciphertext,d.code_key_id,d.percent_off::text,d.max_total_uses,d.redeemed_count,d.usage_adjustment_units,d.max_uses_per_user,
                (SELECT COALESCE(sum(c.units_covered),0)::text FROM shop.discount_uses c WHERE c.discount_id=d.id AND c.status='held'),
                (SELECT COALESCE(sum(c.units_covered),0)::text FROM shop.discount_uses c JOIN shop.orders o ON o.id=c.order_id WHERE c.discount_id=d.id AND o.user_id=$3 AND c.status IN ('held','redeemed')),
                (SELECT COALESCE(sum(c.units_covered),0)::text FROM shop.discount_uses c JOIN shop.orders o ON o.id=c.order_id WHERE c.discount_id=d.id AND o.user_id=$3 AND c.status='held'
                    AND o.payment_status='awaiting_payment' AND NOT EXISTS(SELECT 1 FROM shop.payments p WHERE p.order_id=o.id AND
                        (p.first_payment_observed_at IS NOT NULL OR p.receipt_asset_ref IS NOT NULL OR p.status IN ('observed','confirming','settled','review'))))
            FROM shop.discounts d JOIN shop.discount_revisions r ON r.discount_id=d.id AND r.revision_no=d.revision
            WHERE d.brand_id=$1 AND d.active AND (d.product_id IS NULL OR d.product_id=$2) AND (d.eligible_user_id IS NULL OR d.eligible_user_id=$3)
                AND (d.starts_at IS NULL OR d.starts_at<=clock_timestamp()) AND (d.ends_at IS NULL OR d.ends_at>clock_timestamp())
                AND (d.kind<>'prepaid_entitlement' OR EXISTS(SELECT 1 FROM shop.orders s WHERE s.id=d.source_order_id AND s.user_id=$3 AND s.product_id=$2 AND s.order_kind='prepaid_units' AND s.payment_status='paid'))
            ORDER BY d.id FOR UPDATE OF d
            """, DbValue.Uuid(context.BrandId), DbValue.Uuid(data.ProductId), DbValue.Uuid(data.Member)), row => new Candidate(row.GetGuid(0), row.GetGuid(1), row.GetBinary(2), row.GetString(3), ExactDecimal.Parse(row.GetString(4)),
                row.IsDBNull(5) ? null : row.GetInt64(5), row.GetInt64(6), row.GetInt64(7), row.IsDBNull(8) ? null : row.GetInt64(8), BigInteger.Parse(row.GetString(9), CultureInfo.InvariantCulture), BigInteger.Parse(row.GetString(10), CultureInfo.InvariantCulture), BigInteger.Parse(row.GetString(11), CultureInfo.InvariantCulture)), 1000, ct);
        var coupons = new List<QuoteCoupon>(rows.Count);
        var cipher = new BrandCipher(secrets);
        foreach (Candidate candidate in rows)
        {
            BigInteger? capacity = PricingPolicy.Remaining(candidate.Limit, candidate.Redeemed, candidate.Adjustment, candidate.Held - candidate.Reusable);
            if (candidate.MemberLimit is long memberLimit)
            {
                BigInteger own = BigInteger.Max(0, (BigInteger)memberLimit - candidate.MemberUsed + candidate.Reusable);
                capacity = capacity is BigInteger global ? BigInteger.Min(global, own) : own;
            }
            byte[] plain = cipher.Decrypt(context, "discounts", candidate.Id, "code_ciphertext", candidate.Key, candidate.Code);
            try { coupons.Add(new(candidate.Id, candidate.Revision, Encoding.UTF8.GetString(plain), candidate.Percent, capacity)); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        DateTime now = await session.QuerySingleAsync(new DbStatement("SELECT clock_timestamp()"), row => row.GetUtc(0), ct);
        if (Supply(data with { Now = now }, query.Quantity, query.Kind) is null) throw new PricingRejectedException();
        return CheckoutQuote.Create(new(data.UnitUsdt, data.UnitToman, data.AutoToman, data.FxEnabled, data.Fx, data.MaximumFxAge), data.Presentation, query.Quantity, query.Payment, coupons, now);
    }

    private async Task<Data?> LoadAsync(DbSession session, BrandContext context, long actor, Guid product, CancellationToken ct)
    {
        await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM (SELECT id FROM shop.users WHERE brand_id=$1 AND telegram_id=$2 FOR UPDATE) u", DbValue.Uuid(context.BrandId), DbValue.Bigint(actor)), row => row.GetInt64(0), ct);
        BrandUser member = await BrandUserStore.RequireAsync(session, context, actor, null, ct);
        if (member.Blocked || member.Banned) Reject(session);
        var master = await session.QuerySingleAsync<Master?>(new DbStatement("""
            SELECT p.id,p.active,p.revision,p.fulfillment_kind,p.availability_policy,p.local_first,p.active_supplier_source_id,p.title,p.description,p.icon_ref,p.guidance
            FROM (SELECT 1) seed LEFT JOIN (SELECT * FROM shop.products WHERE id=$1 FOR UPDATE) p ON true
            """, DbValue.Uuid(product)), row => row.IsDBNull(0) ? null : new(row.GetBoolean(1), row.GetInt64(2), row.GetString(3), row.GetString(4), row.GetBoolean(5), row.IsDBNull(6) ? null : row.GetGuid(6), row.GetString(7), String(row, 8), String(row, 9), String(row, 10)), ct);
        if (master is null || !master.Active) return null;
        var brand = await session.QuerySingleAsync(new DbStatement("""
            SELECT public_name,market,policy_version,allow_wallet,allow_crypto,allow_bank,fx_enabled,current_fx::text,fx_source,fx_fetched_at,max_exchange_rate_age_seconds
            FROM shop.brands WHERE id=$1 AND bot_id=$2 AND active FOR UPDATE
            """, DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId)), row => new Brand(row.GetString(0), row.GetString(1), row.GetInt64(2), row.GetBoolean(3), row.GetBoolean(4), row.GetBoolean(5), row.GetBoolean(6),
                row.IsDBNull(7) ? null : new FxRate(ExactDecimal.Parse(row.GetString(7)), row.GetString(8), row.GetUtc(9)), TimeSpan.FromSeconds(row.GetInt64(10))), ct);
        var listing = await session.QuerySingleAsync<Listing?>(new DbStatement("""
            SELECT l.product_id,l.visible,l.revision,l.price_usdt::text,l.price_toman::text,l.auto_price_toman,l.allow_wallet,l.allow_crypto,l.allow_bank,l.min_quantity,l.max_quantity,
                CASE WHEN l.title_mode='override' THEN l.title_override ELSE $3 END,
                CASE l.description_mode WHEN 'override' THEN l.description_override WHEN 'clear' THEN NULL ELSE $4 END,
                CASE l.icon_mode WHEN 'override' THEN l.icon_override WHEN 'clear' THEN NULL ELSE $5 END,
                CASE l.guidance_mode WHEN 'override' THEN l.guidance_override WHEN 'clear' THEN NULL ELSE $6 END
            FROM (SELECT 1) seed LEFT JOIN (SELECT * FROM shop.brand_products WHERE brand_id=$1 AND product_id=$2 FOR UPDATE) l ON true
            """, DbValue.Uuid(context.BrandId), DbValue.Uuid(product), DbValue.Text(master.Title), Text(master.Description), Text(master.Icon), Text(master.Guidance)), row => row.IsDBNull(0) ? null : new Listing(row.GetBoolean(1), row.GetInt64(2), ExactDecimal.Parse(row.GetString(3)),
                row.IsDBNull(4) ? null : ExactDecimal.Parse(row.GetString(4)), row.GetBoolean(5), row.GetBoolean(6), row.GetBoolean(7), row.GetBoolean(8), row.GetInt32(9), row.GetInt32(10), row.GetString(11), String(row, 12), String(row, 13), String(row, 14)), ct);
        if (listing is null || !listing.Visible) return null;
        long local = await session.QuerySingleAsync(new DbStatement("""
            SELECT CASE WHEN EXISTS(SELECT 1 FROM shop.orders WHERE product_id=$1 AND is_preorder AND payment_status IN ('paid','not_required') AND fulfillment_status IN ('waiting_stock','requires_admin'))
                THEN 0 ELSE (SELECT count(*) FROM shop.inventory_items WHERE product_id=$1 AND state='available') END
            """, DbValue.Uuid(product)), row => row.GetInt64(0), ct);
        Source? source = master.Source is Guid id ? await session.QuerySingleAsync<Source?>(new DbStatement("""
            SELECT s.id,s.product_id,s.provider_code,s.external_product_id,s.external_variant_id,s.enabled,s.revision,s.observation_revision,s.observed_source_revision,
                s.observed_account_ref,s.observed_at,s.availability_code,s.available_quantity,s.observed_unit_cost::text,s.observed_currency,s.eligibility_code,s.quote_ref
            FROM (SELECT 1) seed LEFT JOIN (SELECT * FROM shop.product_supplier_sources WHERE id=$1 AND product_id=$2 FOR UPDATE) s ON true
            """, DbValue.Uuid(id), DbValue.Uuid(product)), row => row.IsDBNull(0) ? null : new Source(row.GetGuid(0), row.GetString(2), row.GetString(3), String(row, 4), row.GetBoolean(5), row.GetInt64(6), row.GetInt64(7), row.IsDBNull(8) ? null : row.GetInt64(8), String(row, 9), row.IsDBNull(10) ? null : row.GetUtc(10), row.GetString(11), row.IsDBNull(12) ? null : row.GetInt32(12),
                row.IsDBNull(13) ? null : ExactDecimal.Parse(row.GetString(13)), String(row, 14), row.GetString(15), String(row, 16)), ct) : null;
        DateTime now = await session.QuerySingleAsync(new DbStatement("SELECT clock_timestamp()"), row => row.GetUtc(0), ct);
        return new(product, member.Id, brand.Revision, master.Revision, listing.Revision, new(brand.Name, listing.Title, listing.Description, listing.Icon, listing.Guidance),
            master.Kind, master.Policy, master.LocalFirst, local, listing.UnitUsdt, listing.UnitToman, listing.Automatic, brand.FxEnabled, brand.Fx, brand.MaximumAge,
            brand.Market, brand.Wallet && listing.Wallet, brand.Crypto && listing.Crypto, brand.Bank && listing.Bank, listing.Minimum, listing.Maximum, source, now);
    }

    private CheckoutSupply? Supply(Data data, int quantity, CheckoutKind kind = CheckoutKind.ProductPurchase)
    {
        if (kind == CheckoutKind.PrepaidUnits) return new(0, 0, false, "prepaid");
        if (data.Policy == "local_preorder") return new(data.Local >= quantity ? quantity : 0, 0, data.Local < quantity, "local");
        if (data.Kind == "manual") return new(0, 0, false, "manual");
        int local = data.Kind == "local" || data.LocalFirst ? (int)Math.Min(data.Local, quantity) : 0;
        if (local == quantity) return new(local, 0, false, "local");
        int remote = quantity - local;
        if (data.Kind != "supplier" || data.Source is not { Enabled: true, Availability: "available", Eligibility: "ready_item" } source ||
            source.ObservedSourceRevision != source.Revision || source.ObservedAt is not DateTime time || time > data.Now || data.Now - time > TimeSpan.FromSeconds(30) ||
            source.Quantity < remote || source.Cost is not > 0 || !Label(source.Currency) || !Label(source.Quote) || !accounts.TryGetValue(source.Provider, out string? account) || source.Account != account) return null;
        return new(local, remote, false, "supplier");
    }
    private static bool PaymentAllowed(Data data, CheckoutPayment payment) => payment switch
    {
        CheckoutPayment.Wallet => data.Wallet,
        CheckoutPayment.Crypto => data.Crypto,
        CheckoutPayment.Bank => data.Bank && data.Market == "iran",
        _ => false
    };
    private static int RemoteNeeded(Data data, int quantity) => data.Kind != "supplier" ? 0 : data.LocalFirst ? (int)Math.Max(0, quantity - data.Local) : quantity;
    private static bool SameState(Data a, Data b) => a.Member == b.Member && a.PolicyRevision == b.PolicyRevision && a.ProductRevision == b.ProductRevision && a.ListingRevision == b.ListingRevision &&
        a.Presentation == b.Presentation && a.Kind == b.Kind && a.Policy == b.Policy && a.LocalFirst == b.LocalFirst && a.Local == b.Local && a.UnitUsdt == b.UnitUsdt && a.UnitToman == b.UnitToman && a.AutoToman == b.AutoToman &&
        a.Fx == b.Fx && a.FxEnabled == b.FxEnabled && a.MaximumFxAge == b.MaximumFxAge && a.Wallet == b.Wallet && a.Crypto == b.Crypto && a.Bank == b.Bank && a.Minimum == b.Minimum && a.Maximum == b.Maximum && a.Source == b.Source;
    private static SupplierCatalogResult Normalize(SupplierCatalogResult? result)
    {
        if (result is null || !Enum.IsDefined(result.Status) || result.Quantity < 0 || (result.Status is SupplierCatalogStatus.Available or SupplierCatalogStatus.OutOfStock &&
            (result.UnitCost is not > 0 || !Label(result.Currency) || !Label(result.QuoteRef)))) return new(SupplierCatalogStatus.Error, 0);
        if (result.Status == SupplierCatalogStatus.Available && !result.ReadyItem) return new(SupplierCatalogStatus.Ineligible, 0);
        if (result.Status == SupplierCatalogStatus.Available && result.Quantity == 0) return result with { Status = SupplierCatalogStatus.OutOfStock };
        if (result.Status == SupplierCatalogStatus.OutOfStock && result.Quantity != 0) return new(SupplierCatalogStatus.Error, 0);
        return result.Status is SupplierCatalogStatus.Error or SupplierCatalogStatus.Ineligible ? new(result.Status, 0) : result;
    }
    private static async Task WakeRestockAsync(DbSession session, Guid product, CancellationToken ct)
    {
        bool wake = await session.QuerySingleAsync(new DbStatement("""
            SELECT EXISTS(SELECT 1 FROM shop.product_stock_subscriptions WHERE product_id=$1 AND triggered_at IS NULL AND closed_at IS NULL AND cancelled_at IS NULL)
                AND NOT EXISTS(SELECT 1 FROM shop.jobs WHERE product_id=$1 AND job_kind='evaluate_restock' AND status IN ('pending','retry'))
            """, DbValue.Uuid(product)), row => row.GetBoolean(0), ct);
        if (wake) await OutboxStore.EnqueueAsync(session, JobIntent.Product(JobKind.EvaluateRestock, product), Guid.NewGuid(), DateTime.UtcNow, ct);
    }
    private static void ValidateQuery(CatalogQuery query)
    {
        if (query.ProductId == Guid.Empty || query.Quantity <= 0 || !Enum.IsDefined(query.Kind) || query.Payment is not (CheckoutPayment.Wallet or CheckoutPayment.Crypto or CheckoutPayment.Bank)) throw new PricingRejectedException();
    }
    private static bool Label(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 200 && !value.Any(char.IsControl);
    private static DbValue Text(string? value) => value is null ? DbValue.Null(NpgsqlDbType.Text) : DbValue.Text(value);
    private static DbValue Numeric(decimal? value) => value is decimal number ? DbValue.Numeric(number) : DbValue.Null(NpgsqlDbType.Numeric);
    private static string? String(DbRow row, int index) => row.IsDBNull(index) ? null : row.GetString(index);
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Reject(DbSession session) { session.RejectApplication(); throw new UnauthorizedAccessException("Catalog or quote changed or is unavailable."); }
    private sealed record Brand(string Name, string Market, long Revision, bool Wallet, bool Crypto, bool Bank, bool FxEnabled, FxRate? Fx, TimeSpan MaximumAge);
    private sealed record Master(bool Active, long Revision, string Kind, string Policy, bool LocalFirst, Guid? Source, string Title, string? Description, string? Icon, string? Guidance);
    private sealed record Listing(bool Visible, long Revision, decimal UnitUsdt, decimal? UnitToman, bool Automatic, bool Wallet, bool Crypto, bool Bank, int Minimum, int Maximum, string Title, string? Description, string? Icon, string? Guidance);
    private sealed record Source(Guid Id, string Provider, string ProductRef, string? VariantRef, bool Enabled, long Revision, long ObservationRevision, long? ObservedSourceRevision,
        string? Account, DateTime? ObservedAt, string Availability, int? Quantity, decimal? Cost, string? Currency, string Eligibility, string? Quote);
    private sealed record Data(Guid ProductId, Guid Member, long PolicyRevision, long ProductRevision, long ListingRevision, ProductPresentation Presentation, string Kind, string Policy, bool LocalFirst, long Local,
        decimal UnitUsdt, decimal? UnitToman, bool AutoToman, bool FxEnabled, FxRate? Fx, TimeSpan MaximumFxAge, string Market, bool Wallet, bool Crypto, bool Bank, int Minimum, int Maximum, Source? Source, DateTime Now);
    private sealed record Candidate(Guid Id, Guid Revision, byte[] Code, string Key, decimal Percent, long? Limit, long Redeemed, long Adjustment, long? MemberLimit, BigInteger Held, BigInteger MemberUsed, BigInteger Reusable);
}
