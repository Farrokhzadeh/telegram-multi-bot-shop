using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Data = Platform.Persistence.CatalogState;
using Source = Platform.Persistence.CatalogSource;
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
    public long LocalStock { get; init; }
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
    private readonly Dictionary<string, string> accounts = new(StringComparer.Ordinal);
    private readonly Func<SupplierCatalogRequest, CancellationToken, Task<SupplierCatalogResult>> fetch;
    public CatalogStore(DbSessionFactory sessions, SupplierCredentialGrant grant, SupplierCredentialSnapshot credentials,
        Func<SupplierCatalogRequest, CancellationToken, Task<SupplierCatalogResult>> fetch)
        : this(sessions, credentials.ForGrant(grant).Accounts.Select(value => new SupplierAccount(value.Key, value.Value)), fetch) { }

    public CatalogStore(DbSessionFactory sessions, IEnumerable<SupplierAccount> accounts,
        Func<SupplierCatalogRequest, CancellationToken, Task<SupplierCatalogResult>> fetch)
    {
        this.sessions = sessions; this.fetch = fetch;
        foreach (SupplierAccount account in accounts)
            if (!Label(account.Provider) || !Label(account.AccountRef) || !this.accounts.TryAdd(account.Provider, account.AccountRef)) throw new ArgumentException("One trusted account per provider is required.");
    }

    public Task<CatalogItem?> GetAsync(BrandContext context, long verifiedActorId, Guid productId, CancellationToken ct = default) => GetCoreAsync(context, verifiedActorId, productId, null, ct);
    public Task<CatalogItem?> GetForBrandAsync(BrandContext context, long expectedRevision, long verifiedActorId, Guid productId, CancellationToken ct = default) => GetCoreAsync(context, verifiedActorId, productId, expectedRevision, ct);
    private async Task<CatalogItem?> GetCoreAsync(BrandContext context, long verifiedActorId, Guid productId, long? writerRevision, CancellationToken ct)
    {
        if (writerRevision is < 0) throw new ArgumentOutOfRangeException(nameof(writerRevision));
        var state = await RefreshAsync(context, verifiedActorId, new(productId, 1, CheckoutPayment.Wallet), true, ct, writerRevision);
        if (state is null) return null;
        var display = CheckoutQuote.Create(new(state.UnitUsdt, state.UnitToman, state.AutoToman, state.FxEnabled, state.Fx, state.MaximumFxAge), state.Presentation, 1, CheckoutPayment.Wallet, [], state.Now);
        return new(productId, state.Presentation, state.UnitUsdt, display.Terms.UnitToman, state.AutoToman,
            Supply(state, state.Minimum) is null ? CatalogAvailability.OutOfStock : CatalogAvailability.Available, state.Minimum, state.Maximum, state.Wallet, state.Crypto, state.Bank && state.Market == "iran")
        { LocalStock = state.Local };
    }

    public async Task<CheckoutQuote> QuoteAsync(BrandContext context, long verifiedActorId, CatalogQuery query, CancellationToken ct = default) =>
        (await PrepareAsync(context, verifiedActorId, query, ct)).Quote;

    public async Task<CheckoutQuote> QuoteForBrandAsync(BrandContext context, long expectedRevision, long verifiedActorId, CatalogQuery query, CancellationToken ct = default) =>
        (await PrepareForBrandAsync(context, expectedRevision, verifiedActorId, query, ct)).Quote;
    public Task<PreparedCheckout> PrepareAsync(BrandContext context, long verifiedActorId, CatalogQuery query, CancellationToken ct = default) => PrepareCoreAsync(context, verifiedActorId, query, null, ct);
    public Task<PreparedCheckout> PrepareForBrandAsync(BrandContext context, long expectedRevision, long verifiedActorId, CatalogQuery query, CancellationToken ct = default) => PrepareCoreAsync(context, verifiedActorId, query, expectedRevision, ct);
    private async Task<PreparedCheckout> PrepareCoreAsync(BrandContext context, long verifiedActorId, CatalogQuery query, long? writerRevision, CancellationToken ct)
    {
        if (writerRevision is < 0) throw new ArgumentOutOfRangeException(nameof(writerRevision));
        ValidateQuery(query);
        var state = await RefreshAsync(context, verifiedActorId, query, false, ct, writerRevision);
        if (state is null || Supply(state, query.Quantity, query.Kind) is not CheckoutSupply supply) throw new PricingRejectedException();
        await using var session = await sessions.BeginAsync(ct);
        Data? current = await LoadAsync(session, context, verifiedActorId, query.ProductId, ct, writerRevision);
        if (current is null || !SameState(state, current) || Supply(current, query.Quantity, query.Kind) != supply) Reject(session);
        CheckoutQuote quote = await BuildQuoteAsync(session, context, current, query, ct);
        var prepared = new PreparedCheckout(this, context, verifiedActorId, current.Member, query, quote, supply, current.PolicyRevision, current.ProductRevision,
            current.ListingRevision, supply.RemoteUnits > 0 ? current.Source?.Id : null, supply.RemoteUnits > 0 ? current.Source?.Revision : null, supply.RemoteUnits > 0 ? current.Source?.ObservationRevision : null);
        await BotActivityWriter.RecordAsync(session, context, verifiedActorId, current.Member, BotActivityAction.QuantityEnter, BotActivitySource.Flow, BotActivityOutcome.Accepted,
            new(query.ProductId, Quantity: query.Quantity), Guid.NewGuid(), current.Now, ct);
        await session.CommitAsync(ct);
        return prepared;
    }

    public Task RequireCurrentAsync(DbSession session, BrandContext context, long verifiedActorId, PreparedCheckout prepared, CancellationToken ct = default) => RequireCurrentCoreAsync(session, context, verifiedActorId, prepared, null, ct);
    public Task RequireCurrentForBrandAsync(DbSession session, BrandContext context, long expectedRevision, long verifiedActorId, PreparedCheckout prepared, CancellationToken ct = default) => RequireCurrentCoreAsync(session, context, verifiedActorId, prepared, expectedRevision, ct);
    private async Task RequireCurrentCoreAsync(DbSession session, BrandContext context, long verifiedActorId, PreparedCheckout prepared, long? writerRevision, CancellationToken ct)
    {
        try
        {
            if (!ReferenceEquals(prepared.Owner, this) || prepared.Context != context || prepared.Actor != verifiedActorId ||
                Interlocked.Exchange(ref prepared.Used, 1) != 0 || Stopwatch.GetElapsedTime(prepared.IssuedAt) > TimeSpan.FromSeconds(30)) Reject(session);
            Data? current = await LoadAsync(session, context, verifiedActorId, prepared.Query.ProductId, ct, writerRevision);
            if (current is null || current.Member != prepared.Member || current.PolicyRevision != prepared.PolicyRevision || current.ProductRevision != prepared.ProductRevision || current.ListingRevision != prepared.ListingRevision ||
                Supply(current, prepared.Query.Quantity, prepared.Query.Kind) != prepared.Supply || (prepared.Supply.RemoteUnits > 0 &&
                (current.Source?.Id != prepared.Source || current.Source?.Revision != prepared.SourceRevision || current.Source?.ObservationRevision != prepared.ObservationRevision))) Reject(session);
            CheckoutQuote quote = await BuildQuoteAsync(session, context, current, prepared.Query, ct);
            if (quote.Terms != prepared.Quote.Terms || quote.RequestedPayment != prepared.Quote.RequestedPayment || Stopwatch.GetElapsedTime(prepared.IssuedAt) > TimeSpan.FromSeconds(30)) Reject(session);
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }

    public Task<RestockObservation> ObserveRestockAsync(BrandContext context, long verifiedActorId, Guid productId, CancellationToken ct = default) => ObserveRestockCoreAsync(context, verifiedActorId, productId, null, ct);
    public Task<RestockObservation> ObserveRestockForBrandAsync(BrandContext context, long expectedRevision, long verifiedActorId, Guid productId, CancellationToken ct = default) => ObserveRestockCoreAsync(context, verifiedActorId, productId, expectedRevision, ct);
    private async Task<RestockObservation> ObserveRestockCoreAsync(BrandContext context, long verifiedActorId, Guid productId, long? writerRevision, CancellationToken ct)
    {
        if (writerRevision is < 0) throw new ArgumentOutOfRangeException(nameof(writerRevision));
        Data? state = await RefreshAsync(context, verifiedActorId, new(productId, 1, CheckoutPayment.Wallet), true, ct, writerRevision);
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
        await WriteSupplierAsync(session, context, selected, order, prepared.Supply.RemoteUnits, null, ct);
    }

    internal async Task<string?> BindAcceptedAsync(DbSession session, BrandContext context, Guid product, Guid order, int quantity, CancellationToken ct)
    {
        var data = await session.RunEfStepAsync(false, async (ef, token) =>
        {
            var master = await ef.Data.Set<Entities.Product>().Where(value => value.Id == product).Select(value => new { value.FulfillmentKind, value.ActiveSupplierSourceId }).SingleAsync(token);
            var source = await CatalogQueries.SourceAsync(ef, master.ActiveSupplierSourceId, product, token, true);
            return (Kind: master.FulfillmentKind, Source: source, Now: await ef.BrandClockAsync(context.BrandId, token));
        }, ct);
        Source? source = data.Source;
        string? review = data.Kind != "supplier" ? "source_ineligible" : source is null ? "source_missing" : !source.Enabled ? "source_disabled" :
            !accounts.TryGetValue(source.Provider, out string? account) || source.Account != account ? "account_unverified" :
            source.ObservedSourceRevision != source.Revision || source.ObservedAt is not DateTime observed || observed > data.Now || data.Now - observed > TimeSpan.FromSeconds(30) ? "cost_unverified" :
            source.Eligibility != "ready_item" ? "source_ineligible" : source.Availability != "available" || source.Quantity is not int available || available < quantity ? "stock_shortage" : null;
        if (source is null || source.Cost is not > 0 || source.Currency is null || source.Quote is null || source.ObservedAt is null || source.Account is null)
        {
            await session.RunEfStepAsync(true, async (ef, token) =>
            {
                await ef.RequireOrderCommandAsync(context, order, token);
                ef.Data.Add(new Entities.SupplierRequest { Id = Guid.NewGuid(), OrderId = order, RequestedQuantity = quantity, ProductSupplierSourceId = source?.Id, SourceRevision = source?.Revision, ProviderCode = source?.Provider, AccountRef = source?.Account, ExternalProductId = source?.ProductRef, ExternalVariantId = source?.VariantRef, LocalIntentKey = "order:" + order.ToString("D"), RequestSha256 = SHA256.HashData(order.ToByteArray()), Status = "blocked", ReviewCode = review ?? "cost_unverified" });
                return true;
            }, ct);
            return review ?? "cost_unverified";
        }
        await WriteSupplierAsync(session, context, source, order, quantity, review, ct);
        return review;
    }

    private static async Task WriteSupplierAsync(DbSession session, BrandContext context, Source selected, Guid order, int quantity, string? review, CancellationToken ct)
    {
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
                writer.Write(quantity); writer.Write(ExactDecimal.Format(selected.Cost!.Value)); writer.Write(selected.Currency!);
                writer.Write(selected.Quote!); writer.Write(selected.ObservedAt!.Value.Ticks);
            }
            hash = SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)));
        }
        await session.RunEfStepAsync(true, async (ef, token) =>
        {
            await ef.RequireOrderCommandAsync(context, order, token);
            ef.Data.Add(new Entities.SupplierRequest { Id = id, OrderId = order, RequestedQuantity = quantity, ProductSupplierSourceId = selected.Id, SourceRevision = selected.Revision, ProviderCode = selected.Provider, AccountRef = selected.Account, ExternalProductId = selected.ProductRef, ExternalVariantId = selected.VariantRef, LocalIntentKey = intent, RequestSha256 = hash, Status = review is null ? "pending" : "blocked", CostBaselineAmount = selected.Cost, CostCurrency = selected.Currency, CostQuoteRef = selected.Quote, CostObservedAt = selected.ObservedAt, ReviewCode = review });
            return true;
        }, ct);
        if (review is null) await OutboxStore.EnqueueAsync(session, JobIntent.Supplier(JobKind.SubmitSupplier, context.BrandId, id), order, DateTime.UtcNow, ct);
    }

    private async Task<Data?> RefreshAsync(BrandContext context, long actor, CatalogQuery query, bool display, CancellationToken ct, long? writerRevision = null)
    {
        Data? before;
        await using (var preflight = await sessions.BeginAsync(ct)) before = await LoadAsync(preflight, context, actor, query.ProductId, ct, writerRevision);
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
        Data? current = await LoadAsync(apply, context, actor, query.ProductId, ct, writerRevision);
        if (current is null) return null;
        if (!SameState(before, current) || (query.Kind == CheckoutKind.PrepaidUnits ? 0 : RemoteNeeded(current, quantity)) != remote) Reject(apply);
        if (result is not null && account is not null)
        {
            Source observedSource = current.Source!;
            await WriteObservationAsync(apply, observedSource.Id, observedSource.Revision, observedSource.ObservationRevision, account, result, ct);
            current = await LoadAsync(apply, context, actor, query.ProductId, ct, writerRevision) ?? throw new PricingRejectedException();
        }
        if (Supply(current, current.Minimum) is not null) await WakeRestockAsync(apply, current.ProductId, ct);
        await apply.CommitAsync(ct);
        return current;
    }

    internal async Task<(string Account, SupplierCatalogResult Result)> InspectSourceAsync(string provider, string product, string? variant, CancellationToken ct)
    {
        if (!accounts.TryGetValue(provider, out string? account)) throw new PricingRejectedException();
        SupplierCatalogResult result;
        try { result = Normalize(await fetch(new(provider, account, product, variant, 1), ct)); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { throw new PricingRejectedException(); }
        if (result.Status is not (SupplierCatalogStatus.Available or SupplierCatalogStatus.OutOfStock) || !result.ReadyItem) throw new PricingRejectedException();
        return (account, result);
    }
    internal static async Task WriteObservationAsync(DbSession session, Guid source, long revision, long observation, string account, SupplierCatalogResult result, CancellationToken ct)
    {
        string status = result.Status switch { SupplierCatalogStatus.Available => "available", SupplierCatalogStatus.OutOfStock => "out_of_stock", SupplierCatalogStatus.Ineligible => "ineligible", _ => "error" };
        await session.RunEfStepAsync(true, async (ef, token) =>
        {
            await ef.RequireSourceCommandAsync(source, token);
            await ef.LockSourceAsync(source, token);
            var row = await ef.Data.Set<Entities.ProductSupplierSource>().SingleOrDefaultAsync(value => value.Id == source && value.Revision == revision && value.ObservationRevision == observation && value.Enabled, token);
            if (row is null) ef.RejectScope("Catalog or quote changed or is unavailable.");
            DateTime now = await ef.ClockAsync(ef.Data.Set<Entities.ProductSupplierSource>().Where(value => value.Id == source), token);
            row.ObservationRevision++; row.ObservedSourceRevision = row.Revision; row.ObservedAccountRef = account; row.ObservedAt = now; row.AvailabilityCode = status; row.AvailableQuantity = result.Quantity; row.ObservedUnitCost = result.UnitCost; row.ObservedCurrency = result.Currency; row.QuoteRef = result.QuoteRef;
            row.EligibilityCode = result.Status is SupplierCatalogStatus.Available or SupplierCatalogStatus.OutOfStock ? "ready_item" : result.Status == SupplierCatalogStatus.Ineligible ? "ineligible" : "error";
            row.ObservationErrorCode = result.Status == SupplierCatalogStatus.Error ? "source_unavailable" : null; row.UpdatedAt = now;
            return true;
        }, ct);
    }

    private async Task<CheckoutQuote> BuildQuoteAsync(DbSession session, BrandContext context, Data data, CatalogQuery query, CancellationToken ct)
    {
        if (query.Quantity < data.Minimum || query.Quantity > data.Maximum || !PaymentAllowed(data, query.Payment)) throw new PricingRejectedException();
        var snapshot = await session.RunEfStepAsync(false, async (ef, token) =>
        {
            var coupons = await CatalogQueries.CouponsAsync(ef, context, data.ProductId, data.Member, token, true);
            DateTime now = await ef.BrandClockAsync(context.BrandId, token);
            return (Coupons: coupons, Now: now);
        }, ct);
        if (Supply(data with { Now = snapshot.Now }, query.Quantity, query.Kind) is null) throw new PricingRejectedException();
        return CheckoutQuote.Create(new(data.UnitUsdt, data.UnitToman, data.AutoToman, data.FxEnabled, data.Fx, data.MaximumFxAge), data.Presentation, query.Quantity, query.Payment, snapshot.Coupons, snapshot.Now);
    }

    private async Task<Data?> LoadAsync(DbSession session, BrandContext context, long actor, Guid product, CancellationToken ct, long? writerRevision = null)
    {
        bool active = await session.RunEfStepAsync(false, async (ef, token) =>
        {
            await ef.RequireMembershipAsync(context, actor, false, false, token);
            await ef.LockProductAsync(product, token);
            return await ef.Data.Set<Entities.Product>().AnyAsync(value => value.Id == product && value.Active, token);
        }, ct);
        if (!active) return null;
        if (writerRevision is long revision) await BrandWriterStore.RequireAsync(session, context, revision, ct);
        else await session.AdmitWriterAsync(context, ct);
        return await session.RunEfStepAsync(false, (ef, token) => CatalogQueries.LoadAsync(ef, context, actor, product, token, true), ct);
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
        bool wake = await session.RunEfStepAsync(false, async (ef, token) =>
            await ef.Data.Set<Entities.ProductStockSubscription>().AnyAsync(value => value.ProductId == product && value.TriggeredAt == null && value.ClosedAt == null && value.CancelledAt == null, token) &&
            !await ef.Data.Set<Entities.Job>().AnyAsync(value => value.ProductId == product && value.JobKind == "evaluate_restock" && (value.Status == "pending" || value.Status == "retry"), token), ct);
        if (wake) await OutboxStore.EnqueueAsync(session, JobIntent.Product(JobKind.EvaluateRestock, product), Guid.NewGuid(), DateTime.UtcNow, ct);
    }
    private static void ValidateQuery(CatalogQuery query)
    {
        if (query.ProductId == Guid.Empty || query.Quantity <= 0 || !Enum.IsDefined(query.Kind) || query.Payment is not (CheckoutPayment.Wallet or CheckoutPayment.Crypto or CheckoutPayment.Bank)) throw new PricingRejectedException();
    }
    private static bool Label(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 200 && !value.Any(char.IsControl);
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Reject(DbSession session) { session.RejectApplication(); throw new UnauthorizedAccessException("Catalog or quote changed or is unavailable."); }
}
