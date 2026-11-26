using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

public sealed class SqliteCatalogStore(SqliteSessionFactory sessions)
{
    private readonly SqliteSessionFactory sessions = sessions.RequirePlaintext();

    public Task<Guid[]> VisibleProductsAsync(BrandContext context, long actor, CancellationToken ct = default) =>
        sessions.RunEfAsync(false, null, async (data, token) => await (from listing in data.Data.Set<Entities.BrandProduct>()
                                                                       join product in data.Data.Set<Entities.Product>() on listing.ProductId equals product.Id
                                                                       join brand in data.Data.Set<Entities.Brand>() on listing.BrandId equals brand.Id
                                                                       join member in data.Data.Set<Entities.User>() on brand.Id equals member.BrandId
                                                                       where listing.BrandId == context.BrandId && brand.BotId == context.BotId && brand.Active && member.TelegramId == actor && member.BlockedAt == null && member.BannedAt == null && listing.Visible && product.Active && !brand.RequireChannelJoin
                                                                       orderby listing.ProductId
                                                                       select listing.ProductId).Take(25).ToArrayAsync(token), ct);

    public Task<CatalogItem?> GetAsync(BrandContext context, long actor, Guid product, CancellationToken ct = default) =>
        sessions.RunAsync(false, async (_, tx, token) =>
        {
            var state = await ReadAsync(tx, context, actor, product, token);
            if (state is null) return null;
            var quote = CheckoutQuote.Create(state.Prices, state.Presentation, 1, CheckoutPayment.Wallet, [], DateTime.UtcNow);
            return new CatalogItem(product, state.Presentation, state.Prices.UnitUsdt, quote.Terms.UnitToman,
                state.Prices.AutoToman, Supply(state, state.Minimum) is null ? CatalogAvailability.OutOfStock :
                CatalogAvailability.Available, state.Minimum, state.Maximum, state.Wallet, state.Crypto,
                state.Bank && state.Market == "iran")
            { LocalStock = state.Local };
        }, ct);

    public async Task<CheckoutQuote> QuoteAsync(BrandContext context, long actor, CatalogQuery query,
        CancellationToken ct = default) => (await PrepareAsync(context, actor, query, ct)).Quote;

    public Task<PreparedCheckout> PrepareAsync(BrandContext context, long actor, CatalogQuery query, CancellationToken ct = default)
    {
        if (actor <= 0 || query.ProductId == Guid.Empty || query.Quantity is < 1 or > 10000 ||
            query.Payment is not (CheckoutPayment.Wallet or CheckoutPayment.Crypto or CheckoutPayment.Bank) ||
            !Enum.IsDefined(query.Kind)) throw new PricingRejectedException();
        return sessions.RunAsync(false, async (_, tx, token) =>
        {
            var state = await ReadAsync(tx, context, actor, query.ProductId, token);
            var supply = state is null ? null : Supply(state, query.Quantity, query.Kind);
            if (state is null || supply is null || query.Quantity < state.Minimum || query.Quantity > state.Maximum ||
                !Allowed(state, query.Payment)) throw new PricingRejectedException();
            var coupons = await CouponsAsync(tx, context, state, token);
            var quote = CheckoutQuote.Create(state.Prices, state.Presentation, query.Quantity, query.Payment,
                coupons, DateTime.UtcNow);
            return new PreparedCheckout(this, context, actor, state.Member, query, quote, supply, state.PolicyRevision,
                state.ProductRevision, state.ListingRevision, null, null, null);
        }, ct);
    }

    internal async Task<CheckoutQuote> RequireCurrentAsync(SqliteTransaction tx, BrandContext context, long actor,
        PreparedCheckout prepared, CancellationToken ct)
    {
        if (!ReferenceEquals(prepared.Owner, this) || prepared.Context != context ||
            prepared.Actor != actor || Interlocked.Exchange(ref prepared.Used, 1) != 0 ||
            Stopwatch.GetElapsedTime(prepared.IssuedAt) > TimeSpan.FromSeconds(30))
            throw new PricingRejectedException();
        var state = await ReadAsync(tx, context, actor, prepared.Query.ProductId, ct);
        if (state is null || state.Member != prepared.Member ||
            state.PolicyRevision != prepared.PolicyRevision ||
            state.ProductRevision != prepared.ProductRevision ||
            state.ListingRevision != prepared.ListingRevision ||
            Supply(state, prepared.Query.Quantity, prepared.Query.Kind) != prepared.Supply)
            throw new PricingRejectedException();
        var coupons = await CouponsAsync(tx, context, state, ct);
        CheckoutQuote current = CheckoutQuote.Create(state.Prices, state.Presentation,
            prepared.Query.Quantity, prepared.Query.Payment, coupons, DateTime.UtcNow);
        if (Stopwatch.GetElapsedTime(prepared.IssuedAt) > TimeSpan.FromSeconds(30) ||
            current.Terms != prepared.Quote.Terms || current.RequestedPayment != prepared.Quote.RequestedPayment)
            throw new PricingRejectedException();
        return current;
    }

    public async Task<LocalCheckoutRequest> RequireLocalAsync(BrandContext context, long actor,
        PreparedCheckout prepared, Guid order, CancellationToken ct = default)
    {
        if (order == Guid.Empty || !ReferenceEquals(prepared.Owner, this) || prepared.Context != context ||
            prepared.Actor != actor || Interlocked.Exchange(ref prepared.Used, 1) != 0 ||
            Stopwatch.GetElapsedTime(prepared.IssuedAt) > TimeSpan.FromSeconds(30) ||
            prepared.Query.Payment != CheckoutPayment.Wallet || prepared.Query.Kind != CheckoutKind.ProductPurchase ||
            prepared.Supply.RemoteUnits != 0 || prepared.Supply.WaitingForLocalStock ||
            prepared.Quote.Terms.DiscountId is not null)
            throw new PricingRejectedException();
        return await sessions.RunAsync(false, async (_, tx, token) =>
        {
            var state = await ReadAsync(tx, context, actor, prepared.Query.ProductId, token);
            var supply = state is null ? null : Supply(state, prepared.Query.Quantity, prepared.Query.Kind);
            if (state is null || state.Member != prepared.Member || state.PolicyRevision != prepared.PolicyRevision ||
                state.ProductRevision != prepared.ProductRevision || state.ListingRevision != prepared.ListingRevision ||
                supply != prepared.Supply || Stopwatch.GetElapsedTime(prepared.IssuedAt) > TimeSpan.FromSeconds(30) ||
                (await CouponsAsync(tx, context, state, token)).Count > 0) throw new PricingRejectedException();
            var current = CheckoutQuote.Create(state.Prices, state.Presentation, prepared.Query.Quantity,
                prepared.Query.Payment, [], DateTime.UtcNow);
            if (current.Terms != prepared.Quote.Terms) throw new PricingRejectedException();
            return new LocalCheckoutRequest(order, prepared.Query.ProductId, prepared.Query.Quantity,
                prepared.Quote.Terms.UnitUsdt, prepared.ListingRevision, prepared.ProductRevision, prepared.PolicyRevision);
        }, ct);
    }

    private Task<Data?> ReadAsync(SqliteTransaction tx, BrandContext context, long actor, Guid product, CancellationToken ct) => SqliteSessionFactory.StepEfAsync(tx, false, async (data, token) =>
    {
        var state = await CatalogQueries.LoadAsync(data, context, actor, product, token);
        return state is null ? null : new Data(state.Member, product, state.Kind, state.Policy, state.LocalFirst, state.PolicyRevision, state.ProductRevision, state.ListingRevision, new(state.UnitUsdt, state.UnitToman, state.AutoToman, state.FxEnabled, state.Fx, state.MaximumFxAge), state.Presentation, state.Wallet, state.Crypto, state.Bank, state.Market, state.Minimum, state.Maximum, state.Local);
    }, ct);
    private Task<IReadOnlyList<QuoteCoupon>> CouponsAsync(SqliteTransaction tx, BrandContext context, Data data, CancellationToken ct) => SqliteSessionFactory.StepEfAsync(tx, false, (ef, token) => CatalogQueries.CouponsAsync(ef, context, data.Product, data.Member, token), ct);

    private static CheckoutSupply? Supply(Data data, int quantity, CheckoutKind kind = CheckoutKind.ProductPurchase)
    {
        if (kind == CheckoutKind.PrepaidUnits) return new(0, 0, false, "prepaid");
        if (data.Policy == "local_preorder") return new(data.Local >= quantity ? quantity : 0, 0,
            data.Local < quantity, "local");
        if (data.Kind == "manual") return new(0, 0, false, "manual");
        int local = data.Kind == "local" || data.LocalFirst ? (int)Math.Min(data.Local, quantity) : 0;
        if (local == quantity) return new(local, 0, false, "local");
        return null;
    }
    private static bool Allowed(Data data, CheckoutPayment payment) => payment switch
    {
        CheckoutPayment.Wallet => data.Wallet,
        CheckoutPayment.Crypto => data.Crypto,
        CheckoutPayment.Bank => data.Bank && data.Market == "iran",
        _ => false
    };
    private sealed record Data(Guid Member, Guid Product, string Kind, string Policy, bool LocalFirst, long PolicyRevision,
        long ProductRevision, long ListingRevision, QuotePrices Prices, ProductPresentation Presentation,
        bool Wallet, bool Crypto, bool Bank, string Market, int Minimum, int Maximum, long Local);
}
