using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Platform.Core;
using Platform.Persistence;
using Platform.Security;

namespace Platform.Api;

public sealed record SqliteStageRequest(long UpdateId, long User, string Kind, string Value, long? Anchor = null,
    string? MessageText = null);
public sealed record SqliteStageAck(long UpdateId, long User, long Anchor, Guid Product, long Revision);
public sealed record SqliteStageReceiptDecision(Guid BrandId, bool Approve, long ExpectedRevision,
    Guid CommandId, string Reason);
public sealed record SqliteStageWalletCommand(Guid BrandId, WalletAdjustment Value);
public sealed record SqliteStageAdminIdentity(Guid Id);
public sealed record SqliteStageCouponEditCommand(Guid BrandId, CouponOwnerEdit Value,
    long ExpectedRevision, AuditReason Reason, Guid CommandId);
public sealed record SqliteStageConfigurationSaved(long Revision);
public sealed record SqliteStageButton(string Text, string Data);
public sealed record SqliteStageReply(bool Accepted, string Text = "", bool Edit = false,
    SqliteStageButton[]? Buttons = null, string? Ack = null, Guid? Product = null, long? Revision = null);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SqliteStageRequest))]
[JsonSerializable(typeof(SqliteStageAck))]
[JsonSerializable(typeof(SqliteStageReceiptDecision))]
[JsonSerializable(typeof(SqliteStageWalletCommand))]
[JsonSerializable(typeof(SqliteStageAdminIdentity))]
[JsonSerializable(typeof(SqliteStageCouponEditCommand))]
[JsonSerializable(typeof(SqliteStageConfigurationSaved))]
[JsonSerializable(typeof(AdminChange<BrandPolicyEdit>))]
[JsonSerializable(typeof(AdminChange<MasterProductEdit>))]
[JsonSerializable(typeof(AdminChange<ListingEdit>))]
[JsonSerializable(typeof(StockImport))]
[JsonSerializable(typeof(SqliteStageReply))]
[JsonSerializable(typeof(AdminPage<AdminBrand>))]
[JsonSerializable(typeof(AdminPage<AdminProduct>))]
[JsonSerializable(typeof(BrandPolicyDetail))]
[JsonSerializable(typeof(ExchangeRateMetadata))]
[JsonSerializable(typeof(AdminPage<InventoryMetadata>))]
[JsonSerializable(typeof(AdminPage<AdminOrder>))]
public partial class SqliteStageApiJson : JsonSerializerContext { }

public static class SqliteStagingApi
{
    public static async Task<int> RunAsync(CancellationToken ct = default)
    {
        SqliteStagingConfiguration config;
        byte[] privateKey;
        try
        {
            config = await SqliteStagingConfiguration.LoadAsync(ct);
            privateKey = config.ReadApiKey();
        }
        catch (Exception error) when (error is InvalidOperationException or UnauthorizedAccessException
            or IOException or ArgumentException)
        {
            Console.Error.WriteLine("The isolated SQLite API staging configuration is unavailable.");
            return HostBootstrap.ConfigurationRequiredExitCode;
        }
        try
        {
            var ingress = new SqliteStagingIngress(config.Sessions);
            var catalog = new SqliteCatalogStore(config.Sessions);
            var screens = new SqliteCustomerScreens(config.Sessions);
            var checkout = new SqlitePreparedCheckoutStore(config.Sessions);
            var admin = new SqliteAdminCatalogStore(config.Sessions);
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:18081");
            await using var app = builder.Build();
            app.MapGet("/health/sqlite", async (HttpContext http) =>
            {
                if (!Authorize(http, privateKey)) return Results.Unauthorized();
                await ingress.NextOffsetAsync(config.Context, http.RequestAborted);
                http.Response.Headers["X-Sqlite-Format"] = "2";
                return Results.Text("sqlite:ready");
            });
            app.MapGet("/staging/cursor", async (HttpContext http) =>
            {
                if (!Authorize(http, privateKey)) return Results.Unauthorized();
                long next = await ingress.NextOffsetAsync(config.Context, http.RequestAborted);
                return Results.Text(next.ToString(CultureInfo.InvariantCulture));
            });
            app.MapMethods("/staging/update", ["POST"], (RequestDelegate)(async (HttpContext http) =>
            {
                if (!Authorize(http, privateKey)) { http.Response.StatusCode = 401; return; }
                try
                {
                    http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()!
                        .MaxRequestBodySize = 8192;
                    var request = await JsonSerializer.DeserializeAsync(http.Request.Body,
                        SqliteStageApiJson.Default.SqliteStageRequest, http.RequestAborted);
                    if (request is null || request.User != config.Recipient ||
                        request.Value.Length > 4096 || request.MessageText?.Length > 4096)
                        throw new UnauthorizedAccessException();
                    SqliteStageReply reply = await ProcessAsync(request, config, catalog, checkout, screens, ingress,
                        http.RequestAborted);
                    http.Response.ContentType = "application/json";
                    await JsonSerializer.SerializeAsync(http.Response.Body, reply, SqliteStageApiJson.Default.SqliteStageReply,
                        http.RequestAborted);
                }
                catch (Exception error) when (error is UnauthorizedAccessException or ArgumentException
                    or PricingRejectedException or FormatException or InvalidOperationException)
                {
                    http.Response.StatusCode = 409;
                    http.Response.ContentType = "application/json";
                    await JsonSerializer.SerializeAsync(http.Response.Body, new SqliteStageReply(false,
                        "This staging action is unavailable. Open /shop again."),
                        SqliteStageApiJson.Default.SqliteStageReply, http.RequestAborted);
                }
            }));
            app.MapMethods("/staging/ack", ["POST"], (RequestDelegate)(async (HttpContext http) =>
            {
                if (!Authorize(http, privateKey)) { http.Response.StatusCode = 401; return; }
                try
                {
                    var request = await JsonSerializer.DeserializeAsync(http.Request.Body,
                        SqliteStageApiJson.Default.SqliteStageAck, http.RequestAborted);
                    if (request is null || request.User != config.Recipient || request.Anchor <= 0 ||
                        request.Product == Guid.Empty || request.UpdateId < 0 ||
                        await ingress.NextOffsetAsync(config.Context, http.RequestAborted) <= request.UpdateId)
                        throw new UnauthorizedAccessException();
                    _ = await ingress.UpdateFlowAsync(config.Context, config.SyntheticActor, "catalog",
                        request.Product, null, request.Anchor, request.Revision, http.RequestAborted);
                    http.Response.StatusCode = 204;
                }
                catch (Exception error) when (error is UnauthorizedAccessException or ArgumentException)
                { http.Response.StatusCode = 409; }
            }));
            app.MapGet("/admin/brands", async (HttpContext http) =>
            {
                if (!Authorize(http, privateKey)) return Results.Unauthorized();
                try
                {
                    var actor = await AuthenticateAsync(http, config);
                    var page = await admin.BrandsAsync(actor, After(http), Limit(http), http.RequestAborted);
                    await JsonSerializer.SerializeAsync(http.Response.Body, page, SqliteStageApiJson.Default.AdminPageAdminBrand,
                        http.RequestAborted);
                    return Results.Empty;
                }
                catch (Exception error) when (error is UnauthorizedAccessException or AdminAuthenticationException
                    or ArgumentException or FormatException)
                { return Results.Unauthorized(); }
            });
            app.MapGet("/admin/products", async (HttpContext http) =>
            {
                if (!Authorize(http, privateKey)) return Results.Unauthorized();
                try
                {
                    var actor = await AuthenticateAsync(http, config);
                    var page = await admin.ProductsAsync(actor, After(http), Limit(http), http.RequestAborted);
                    await JsonSerializer.SerializeAsync(http.Response.Body, page, SqliteStageApiJson.Default.AdminPageAdminProduct,
                        http.RequestAborted);
                    return Results.Empty;
                }
                catch (Exception error) when (error is UnauthorizedAccessException or AdminAuthenticationException
                    or ArgumentException or FormatException)
                { return Results.Unauthorized(); }
            });
            app.MapGet("/admin/brands/{brand:guid}/policy", async (HttpContext http, Guid brand) =>
            {
                if (!Authorize(http, privateKey) || brand != config.Context.BrandId) return Results.Unauthorized();
                try
                {
                    var actor = await AuthenticateAsync(http, config);
                    var detail = await admin.PolicyAsync(actor, config.Context, http.RequestAborted);
                    await JsonSerializer.SerializeAsync(http.Response.Body, detail, SqliteStageApiJson.Default.BrandPolicyDetail,
                        http.RequestAborted);
                    return Results.Empty;
                }
                catch (Exception error) when (error is UnauthorizedAccessException or AdminAuthenticationException)
                { return Results.Unauthorized(); }
            });
            app.MapGet("/admin/brands/{brand:guid}/fx", async (HttpContext http, Guid brand) =>
            {
                if (!Authorize(http, privateKey) || brand != config.Context.BrandId) return Results.Unauthorized();
                try
                {
                    var actor = await AuthenticateAsync(http, config);
                    var rate = await admin.FxAsync(actor, config.Context, http.RequestAborted);
                    await JsonSerializer.SerializeAsync(http.Response.Body, rate, SqliteStageApiJson.Default.ExchangeRateMetadata,
                        http.RequestAborted);
                    return Results.Empty;
                }
                catch (Exception error) when (error is UnauthorizedAccessException or AdminAuthenticationException)
                { return Results.Unauthorized(); }
            });
            app.MapGet("/admin/products/{product:guid}/inventory", async (HttpContext http, Guid product) =>
            {
                if (!Authorize(http, privateKey)) return Results.Unauthorized();
                try
                {
                    var actor = await AuthenticateAsync(http, config);
                    var page = await admin.InventoryAsync(actor, product, After(http), Limit(http), http.RequestAborted);
                    await JsonSerializer.SerializeAsync(http.Response.Body, page, SqliteStageApiJson.Default.AdminPageInventoryMetadata,
                        http.RequestAborted);
                    return Results.Empty;
                }
                catch (Exception error) when (error is UnauthorizedAccessException or AdminAuthenticationException
                    or ArgumentException or FormatException)
                { return Results.Unauthorized(); }
            });
            app.MapGet("/v1/admin/orders", async (HttpContext http) =>
            {
                if (!Authorize(http, privateKey)) return Results.Unauthorized();
                try
                {
                    var actor = await AuthenticateAsync(http, config);
                    if (http.Request.Query.TryGetValue("brandId", out var specified) &&
                        (!Guid.TryParse(specified.ToString(), out var brand) ||
                            brand != config.Context.BrandId))
                        throw new UnauthorizedAccessException();
                    Guid? member = http.Request.Query.TryGetValue("memberId", out var memberText)
                        ? Guid.Parse(memberText.ToString()) : null;
                    Guid? order = http.Request.Query.TryGetValue("orderId", out var orderText)
                        ? Guid.Parse(orderText.ToString()) : null;
                    var page = await new SqliteAdminOrderStore(config.Sessions).SearchAsync(actor,
                        config.Context, member, order, After(http), Limit(http), http.RequestAborted);
                    http.Response.ContentType = "application/json";
                    await JsonSerializer.SerializeAsync(http.Response.Body, page,
                        SqliteStageApiJson.Default.AdminPageAdminOrder, http.RequestAborted);
                    return Results.Empty;
                }
                catch (Exception error) when (error is UnauthorizedAccessException or AdminAuthenticationException
                    or ArgumentException or FormatException)
                { return Results.Unauthorized(); }
            });
            app.MapMethods("/v1/admin/inventory/import", ["POST"],
                (RequestDelegate)(async (HttpContext http) =>
            {
                if (!Authorize(http, privateKey)) { http.Response.StatusCode = 401; return; }
                try
                {
                    var actor = await AuthenticateAsync(http, config);
                    http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()!
                        .MaxRequestBodySize = 131072;
                    var input = await JsonSerializer.DeserializeAsync(http.Request.Body,
                        SqliteStageApiJson.Default.StockImport, http.RequestAborted);
                    if (input is null || input.ProductId == Guid.Empty || input.CommandId == Guid.Empty ||
                        input.Items is not { Length: >= 1 and <= 32 })
                        throw new UnauthorizedAccessException();
                    SqliteInventoryImportStore.RequireSynthetic(input.Items);
                    await new SqliteInventoryImportStore(config.Sessions).ImportAsync(actor, input.ProductId, input.Items, input.CommandId, http.RequestAborted);
                    http.Response.StatusCode = 204;
                }
                catch (Exception error) when (error is UnauthorizedAccessException or AdminAuthenticationException)
                { http.Response.StatusCode = 401; }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException
                    or FormatException or OverflowException or JsonException)
                { http.Response.StatusCode = 409; }
                catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException)
                { http.Response.StatusCode = 503; }
            }));
            app.MapMethods("/v1/admin/wallet-adjustments", ["POST"],
                (RequestDelegate)(async (HttpContext http) =>
            {
                if (!Authorize(http, privateKey)) { http.Response.StatusCode = 401; return; }
                try
                {
                    var actor = await AuthenticateAsync(http, config);
                    http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()!
                        .MaxRequestBodySize = 4096;
                    var input = await JsonSerializer.DeserializeAsync(http.Request.Body,
                        SqliteStageApiJson.Default.SqliteStageWalletCommand, http.RequestAborted);
                    if (input is null || input.BrandId != config.Context.BrandId ||
                        input.Value is null) throw new UnauthorizedAccessException();
                    Guid entry = await new SqliteWalletAdministrationStore(config.Sessions).AdjustAsync(
                        actor, config.Context, input.Value, http.RequestAborted);
                    http.Response.ContentType = "application/json";
                    await JsonSerializer.SerializeAsync(http.Response.Body,
                        new SqliteStageAdminIdentity(entry),
                        SqliteStageApiJson.Default.SqliteStageAdminIdentity, http.RequestAborted);
                }
                catch (Exception error) when (error is UnauthorizedAccessException or AdminAuthenticationException)
                { http.Response.StatusCode = 401; }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException
                    or FormatException or OverflowException)
                { http.Response.StatusCode = 409; }
            }));
            app.MapMethods("/v1/admin/brands/{brand:guid}", ["PATCH"],
                (RequestDelegate)(async (HttpContext http) =>
            {
                if (!Authorize(http, privateKey)) { http.Response.StatusCode = 401; return; }
                try
                {
                    var actor = await AuthenticateAsync(http, config);
                    if (!Guid.TryParse(http.Request.RouteValues["brand"]?.ToString(), out var brand) ||
                        brand != config.Context.BrandId) throw new UnauthorizedAccessException();
                    http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()!
                        .MaxRequestBodySize = 8192;
                    var input = await JsonSerializer.DeserializeAsync(http.Request.Body,
                        SqliteStageApiJson.Default.AdminChangeBrandPolicyEdit, http.RequestAborted);
                    if (input is null || input.Value is null) throw new UnauthorizedAccessException();
                    long revision = await new SqliteAdminCatalogWriteStore(config.Sessions).PolicyAsync(
                        actor, config.Context, input.Value, input.ExpectedRevision,
                        input.CommandId, http.RequestAborted);
                    http.Response.ContentType = "application/json";
                    await JsonSerializer.SerializeAsync(http.Response.Body,
                        new SqliteStageConfigurationSaved(revision),
                        SqliteStageApiJson.Default.SqliteStageConfigurationSaved,
                        http.RequestAborted);
                }
                catch (Exception error) when (error is UnauthorizedAccessException or AdminAuthenticationException)
                { http.Response.StatusCode = 401; }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException
                    or FormatException or OverflowException)
                { http.Response.StatusCode = 409; }
            }));
            app.MapMethods("/v1/admin/products/{id:guid}", ["PUT"],
                (RequestDelegate)(async (HttpContext http) =>
            {
                if (!Authorize(http, privateKey)) { http.Response.StatusCode = 401; return; }
                try
                {
                    var actor = await AuthenticateAsync(http, config);
                    if (!Guid.TryParse(http.Request.RouteValues["id"]?.ToString(), out var product) ||
                        product == Guid.Empty) throw new UnauthorizedAccessException();
                    http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()!
                        .MaxRequestBodySize = 8192;
                    var input = await JsonSerializer.DeserializeAsync(http.Request.Body,
                        SqliteStageApiJson.Default.AdminChangeMasterProductEdit, http.RequestAborted);
                    if (input is null || input.Value is null) throw new UnauthorizedAccessException();
                    long revision = await new SqliteAdminCatalogWriteStore(config.Sessions).ProductAsync(
                        actor, product, input.Value, input.ExpectedRevision, input.CommandId,
                        http.RequestAborted);
                    http.Response.ContentType = "application/json";
                    await JsonSerializer.SerializeAsync(http.Response.Body,
                        new SqliteStageConfigurationSaved(revision),
                        SqliteStageApiJson.Default.SqliteStageConfigurationSaved,
                        http.RequestAborted);
                }
                catch (Exception error) when (error is UnauthorizedAccessException or AdminAuthenticationException)
                { http.Response.StatusCode = 401; }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException
                    or FormatException or OverflowException)
                { http.Response.StatusCode = 409; }
            }));
            app.MapMethods("/v1/admin/products/{id:guid}/brands/{brand:guid}", ["PUT"],
                (RequestDelegate)(async (HttpContext http) =>
            {
                if (!Authorize(http, privateKey)) { http.Response.StatusCode = 401; return; }
                try
                {
                    var actor = await AuthenticateAsync(http, config);
                    if (!Guid.TryParse(http.Request.RouteValues["id"]?.ToString(), out var product) ||
                        product == Guid.Empty ||
                        !Guid.TryParse(http.Request.RouteValues["brand"]?.ToString(), out var brand) ||
                        brand != config.Context.BrandId) throw new UnauthorizedAccessException();
                    http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()!
                        .MaxRequestBodySize = 8192;
                    var input = await JsonSerializer.DeserializeAsync(http.Request.Body,
                        SqliteStageApiJson.Default.AdminChangeListingEdit, http.RequestAborted);
                    if (input is null || input.Value is null) throw new UnauthorizedAccessException();
                    long revision = await new SqliteAdminCatalogWriteStore(config.Sessions).ListingAsync(
                        actor, config.Context, product, input.Value, input.ExpectedRevision,
                        input.CommandId, http.RequestAborted);
                    http.Response.ContentType = "application/json";
                    await JsonSerializer.SerializeAsync(http.Response.Body,
                        new SqliteStageConfigurationSaved(revision),
                        SqliteStageApiJson.Default.SqliteStageConfigurationSaved,
                        http.RequestAborted);
                }
                catch (Exception error) when (error is UnauthorizedAccessException or AdminAuthenticationException)
                { http.Response.StatusCode = 401; }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException
                    or FormatException or OverflowException)
                { http.Response.StatusCode = 409; }
            }));
            app.MapMethods("/v1/admin/coupons/{id:guid}", ["PATCH"],
                (RequestDelegate)(async (HttpContext http) =>
            {
                if (!Authorize(http, privateKey)) { http.Response.StatusCode = 401; return; }
                try
                {
                    var actor = await AuthenticateAsync(http, config);
                    if (!Guid.TryParse(http.Request.RouteValues["id"]?.ToString(), out var coupon) ||
                        coupon == Guid.Empty) throw new UnauthorizedAccessException();
                    http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()!
                        .MaxRequestBodySize = 8192;
                    var input = await JsonSerializer.DeserializeAsync(http.Request.Body,
                        SqliteStageApiJson.Default.SqliteStageCouponEditCommand, http.RequestAborted);
                    if (input is null || input.BrandId != config.Context.BrandId ||
                        input.Value is null) throw new UnauthorizedAccessException();
                    long revision = await new SqliteCouponAdministrationStore(config.Sessions).EditAsync(
                        config.Context, actor, coupon, input.ExpectedRevision, input.Value,
                        input.Reason, input.CommandId, http.RequestAborted);
                    http.Response.ContentType = "application/json";
                    await JsonSerializer.SerializeAsync(http.Response.Body,
                        new SqliteStageConfigurationSaved(revision),
                        SqliteStageApiJson.Default.SqliteStageConfigurationSaved,
                        http.RequestAborted);
                }
                catch (Exception error) when (error is UnauthorizedAccessException or AdminAuthenticationException)
                { http.Response.StatusCode = 401; }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException
                    or FormatException or OverflowException)
                { http.Response.StatusCode = 409; }
            }));
            app.MapMethods("/v1/admin/manual-deposits/{id:guid}/decision", ["POST"],
                (RequestDelegate)(async (HttpContext http) =>
            {
                if (!Authorize(http, privateKey)) { http.Response.StatusCode = 401; return; }
                try
                {
                    var actor = await AuthenticateAsync(http, config);
                    if (!Guid.TryParse(http.Request.RouteValues["id"]?.ToString(), out var payment) ||
                        payment == Guid.Empty) throw new UnauthorizedAccessException();
                    http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()!
                        .MaxRequestBodySize = 4096;
                    var input = await JsonSerializer.DeserializeAsync(http.Request.Body,
                        SqliteStageApiJson.Default.SqliteStageReceiptDecision, http.RequestAborted);
                    if (input is null || input.BrandId != config.Context.BrandId ||
                        input.CommandId == Guid.Empty) throw new UnauthorizedAccessException();
                    await new SqliteBankReviewStore(config.Sessions).DecideAsync(config.Context,
                        actor, payment, input.Approve, input.ExpectedRevision, input.CommandId,
                        input.Reason, http.RequestAborted);
                    http.Response.StatusCode = 204;
                }
                catch (Exception error) when (error is UnauthorizedAccessException or AdminAuthenticationException)
                { http.Response.StatusCode = 401; }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException)
                { http.Response.StatusCode = 409; }
            }));
            Console.WriteLine("Managed SQLite staging API is starting.");
            await app.RunAsync();
            return 0;
        }
        finally { CryptographicOperations.ZeroMemory(privateKey); }
    }

    private static async Task<SqliteStageReply> ProcessAsync(SqliteStageRequest request,
        SqliteStagingConfiguration config, SqliteCatalogStore catalog, SqlitePreparedCheckoutStore checkout, SqliteCustomerScreens screens,
        SqliteStagingIngress ingress, CancellationToken ct)
    {
        if (request.Kind is not ("message" or "callback") || request.Value.Length is < 1 or > 4096 ||
            !await ingress.AdmitAsync(config.Context, request.UpdateId, ct))
            return new(false);
        long actor = config.SyntheticActor;
        var context = config.Context;
        if (request.Kind == "message")
        {
            string command = request.Value.Split(' ', 2)[0].Split('@', 2)[0];
            if (command is "/start" or "/shop" or "/checkoutpilot" or "/menu")
            {
                Guid[] ids = await catalog.VisibleProductsAsync(context, actor, ct);
                if (ids.Length == 0) return new(true, "No staging products are currently listed.");
                CatalogItem? item = await catalog.GetAsync(context, actor, ids[0], ct);
                if (item is null) return new(true, "The staging catalog is unavailable.");
                SqliteStagingFlow flow = await ingress.ReadFlowAsync(context, actor, ct);
                return new(true, "Akanti staging shop\n" + item.Presentation.Title +
                    "\nPrice: " + item.UnitUsdt.ToString(CultureInfo.InvariantCulture) + " USDT\nAvailable: " +
                    (item.LocalStock > 0 ? "yes" : "no"), false,
                    [new("Select product", "item"), new("Wallet", "wallet"), new("Orders", "orders")],
                    "catalog", item.ProductId, flow.Revision);
            }
            if (command == "/wallet") return await WalletAsync(screens, config, ct);
            if (command is "/orders" or "/transactions" or "/deposits")
                return await ScreenAsync(screens, config, command, ct);
            if (command == "/support")
            {
                CustomerHome home = await screens.HomeAsync(context, actor, ct);
                return new(true, home.Contact is null ? "Support contact is not configured." : "Support: @" + home.Contact);
            }
            if (command == "/language")
            {
                CustomerHome home = await screens.HomeAsync(context, actor, ct);
                await screens.SetLanguageAsync(context, actor, home.Language == "en" ? "fa" : "en", ct);
                return new(true, "Staging language updated.");
            }
            return new(false);
        }
        if (request.Anchor is not > 0) return new(false);
        string choice = request.Value.StartsWith("ak:", StringComparison.Ordinal) ? request.Value[3..] : "";
        if (choice == "wallet") return await WalletAsync(screens, config, ct);
        if (choice == "orders") return await ScreenAsync(screens, config, "/orders", ct);
        SqliteStagingFlow state = await ingress.ReadFlowAsync(context, actor, ct);
        if (state.Anchor != request.Anchor || state.Product is not Guid product) return new(false);
        if (choice == "item" && state.State == "catalog")
        {
            CatalogItem? item = await catalog.GetAsync(context, actor, product, ct);
            if (item is null) return new(true, "Product is unavailable.", true);
            int max = Math.Min(5, item.Maximum);
            var quantities = Enumerable.Range(item.Minimum, Math.Max(0, max - item.Minimum + 1))
                .Select(n => new SqliteStageButton(n.ToString(CultureInfo.InvariantCulture), "qty:" + n)).ToArray();
            if (quantities.Length == 0) return new(true, "No staging stock is available.", true);
            _ = await ingress.UpdateFlowAsync(context, actor, "quantity", product, null, request.Anchor,
                state.Revision, ct);
            return new(true, "Choose quantity for " + item.Presentation.Title, true, quantities);
        }
        if (choice.StartsWith("qty:", StringComparison.Ordinal) && state.State == "quantity" &&
            int.TryParse(choice.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out int quantity) &&
            quantity is >= 1 and <= 5)
        {
            var options = new List<SqliteStageButton>();
            foreach (var (payment, kind, label) in new[]
            {
                (CheckoutPayment.Wallet, CheckoutKind.ProductPurchase, "goods-wallet"),
                (CheckoutPayment.Crypto, CheckoutKind.ProductPurchase, "goods-crypto"),
                (CheckoutPayment.Bank, CheckoutKind.ProductPurchase, "goods-bank"),
                (CheckoutPayment.Wallet, CheckoutKind.PrepaidUnits, "prepaid-wallet"),
                (CheckoutPayment.Crypto, CheckoutKind.PrepaidUnits, "prepaid-crypto"),
                (CheckoutPayment.Bank, CheckoutKind.PrepaidUnits, "prepaid-bank")
            })
            {
                try
                {
                    CheckoutQuote quote = await catalog.QuoteAsync(context, actor,
                        new(product, quantity, payment, kind), ct);
                    if (quote.Payable.Amount > 0)
                        options.Add(new(label, "method:" + label + ":" + quantity.ToString(CultureInfo.InvariantCulture)));
                }
                catch (PricingRejectedException) { }
            }
            if (options.Count == 0) return new(true, "No staging payment method is available.", true);
            return new(true, "Staging quantity\nQuantity: " + quantity.ToString(CultureInfo.InvariantCulture),
                true, options.ToArray());
        }
        if (choice.StartsWith("method:", StringComparison.Ordinal) && state.State == "quantity")
        {
            string[] parts = choice.Split(':', StringSplitOptions.None);
            if (parts.Length != 3 || !int.TryParse(parts[2], NumberStyles.None,
                CultureInfo.InvariantCulture, out int selectedQuantity) || selectedQuantity is < 1 or > 5 ||
                request.MessageText != "Staging quantity\nQuantity: " + selectedQuantity.ToString(CultureInfo.InvariantCulture))
                throw new UnauthorizedAccessException("Staging quantity selection is unavailable.");
            (CheckoutPayment method, CheckoutKind kind) = parts[1] switch
            {
                "goods-wallet" => (CheckoutPayment.Wallet, CheckoutKind.ProductPurchase),
                "goods-crypto" => (CheckoutPayment.Crypto, CheckoutKind.ProductPurchase),
                "goods-bank" => (CheckoutPayment.Bank, CheckoutKind.ProductPurchase),
                "prepaid-wallet" => (CheckoutPayment.Wallet, CheckoutKind.PrepaidUnits),
                "prepaid-crypto" => (CheckoutPayment.Crypto, CheckoutKind.PrepaidUnits),
                "prepaid-bank" => (CheckoutPayment.Bank, CheckoutKind.PrepaidUnits),
                _ => throw new UnauthorizedAccessException("Staging payment method is unavailable.")
            };
            var prepared = await catalog.PrepareAsync(context, actor,
                new(product, selectedQuantity, method, kind), ct);
            if (prepared.Quote.Payable.Amount <= 0) throw new PricingRejectedException();
            Guid order = Guid.CreateVersion7();
            _ = await ingress.UpdateFlowAsync(context, actor, "payment", product, order, request.Anchor,
                state.Revision, ct);
            return new(true, QuoteText(prepared.Quote, method, kind), true,
                [new("Confirm staging checkout", "pay:" + order.ToString("N")), new("Cancel", "cancel")]);
        }
        if (choice == "cancel" && state.State == "payment")
        {
            _ = await ingress.UpdateFlowAsync(context, actor, "idle", null, null, null, state.Revision, ct);
            return new(true, "Staging checkout cancelled.", true);
        }
        if (choice.StartsWith("pay:", StringComparison.Ordinal) && state.State == "payment" &&
            Guid.TryParseExact(choice[4..], "N", out var orderId) && state.Order == orderId)
        {
            if (request.MessageText is not string confirmed || !confirmed.StartsWith("Staging quote\nQuantity: ", StringComparison.Ordinal))
                throw new UnauthorizedAccessException("The confirmed quote is unavailable.");
            string count = confirmed.Split('\n', StringSplitOptions.None).ElementAtOrDefault(1) ?? "";
            if (!int.TryParse(count.AsSpan("Quantity: ".Length), NumberStyles.None, CultureInfo.InvariantCulture, out int payableQuantity) ||
                payableQuantity is < 1 or > 5) throw new UnauthorizedAccessException();
            string[] lines = confirmed.Split('\n', StringSplitOptions.None);
            if (lines.Length != 6 || !lines[4].StartsWith("Method: ", StringComparison.Ordinal) ||
                !lines[5].StartsWith("Kind: ", StringComparison.Ordinal) ||
                !Enum.TryParse<CheckoutPayment>(lines[4].AsSpan(8), out var method) ||
                !Enum.TryParse<CheckoutKind>(lines[5].AsSpan(6), out var kind) ||
                method is not (CheckoutPayment.Wallet or CheckoutPayment.Crypto or CheckoutPayment.Bank) ||
                kind is not (CheckoutKind.ProductPurchase or CheckoutKind.PrepaidUnits) ||
                lines[4] != "Method: " + method || lines[5] != "Kind: " + kind)
                throw new UnauthorizedAccessException("The confirmed staging payment method is unavailable.");
            var prepared = await catalog.PrepareAsync(context, actor,
                new(product, payableQuantity, method, kind), ct);
            if (confirmed != QuoteText(prepared.Quote, method, kind))
                return new(true, "The staging quote changed. Open /shop to review the new price.", true);
            var purchase = new CheckoutRequest(orderId, product, payableQuantity, method,
                kind, method == CheckoutPayment.Crypto ? CryptoAsset.UsdtTrc20 : null);
            CheckoutResult result = await checkout.PurchaseAsync(context, actor,
                purchase, prepared, catalog, ct);
            if (result.Outcome is CheckoutOutcome.Purchased or CheckoutOutcome.Replayed or CheckoutOutcome.AwaitingPayment)
            {
                _ = await ingress.UpdateFlowAsync(context, actor, "idle", null, null, null, state.Revision, ct);
                return new(true, result.Outcome == CheckoutOutcome.AwaitingPayment
                    ? "Staging order accepted. Awaiting verified external payment."
                    : kind == CheckoutKind.PrepaidUnits
                        ? "Staging prepaid entitlement issued. Private delivery is queued."
                        : "Staging order accepted. Delivery is queued.", true);
            }
            return new(true, "Checkout was not accepted: " + result.Outcome + ". Open /shop to retry.", true);
        }
        return new(false);
    }

    private static string QuoteText(CheckoutQuote quote, CheckoutPayment method, CheckoutKind kind) =>
        "Staging quote\nQuantity: " + quote.Terms.Quantity.ToString(CultureInfo.InvariantCulture) +
        "\nTotal: " + quote.Payable.Amount.ToString(CultureInfo.InvariantCulture) +
        " " + quote.Payable.Currency + "\nProduct: " + quote.Terms.Product.Title +
        "\nMethod: " + method + "\nKind: " + kind;

    private static async Task<SqliteStageReply> WalletAsync(SqliteCustomerScreens screens,
        SqliteStagingConfiguration config, CancellationToken ct)
    {
        var home = await screens.HomeAsync(config.Context, config.SyntheticActor, ct);
        return new(true, "Staging wallet balance: " + home.Balance.ToString(CultureInfo.InvariantCulture) +
            " USD\nOrders: " + home.Orders.ToString(CultureInfo.InvariantCulture));
    }

    private static async Task<SqliteStageReply> ScreenAsync(SqliteCustomerScreens screens,
        SqliteStagingConfiguration config, string command, CancellationToken ct)
    {
        CustomerScreenList kind = command switch
        {
            "/orders" => CustomerScreenList.Orders,
            "/transactions" => CustomerScreenList.Transactions,
            "/deposits" => CustomerScreenList.Deposits,
            _ => throw new UnauthorizedAccessException()
        };
        var result = await screens.ListAsync(config.Context, config.SyntheticActor, kind, "all", 0, ct);
        var text = (result.Entries.Count == 0 ? "No entries." : string.Join("\n", result.Entries.Select(x =>
            x.Title + " — " + x.Status + " (" + x.Id.ToString("N")[..12] + ")"))) +
            (result.HasNext ? "\nMore entries available in the admin console." : "");
        return new(true, text);
    }

    private static bool Authorize(HttpContext http, byte[] key)
    {
        string raw = http.Request.Headers["X-Staging-Key"].ToString();
        if (raw.Length != 64 || !raw.All(c => char.IsAsciiHexDigit(c))) return false;
        byte[] presented = Convert.FromHexString(raw);
        try { return CryptographicOperations.FixedTimeEquals(key, presented); }
        finally { CryptographicOperations.ZeroMemory(presented); }
    }

    private static async Task<AuthenticatedAdmin> AuthenticateAsync(HttpContext http, SqliteStagingConfiguration config)
    {
        string authorization = http.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal) || authorization.Length > 120)
            throw new AdminAuthenticationException();
        using var token = AdminToken.Parse(authorization[7..]);
        return await SqliteAdminSessionStore.AuthenticateAsync(config.Sessions, token, http.RequestAborted);
    }
    private static Guid? After(HttpContext http) => Guid.TryParse(http.Request.Query["after"].ToString(), out var id) ? id : null;
    private static int Limit(HttpContext http) => int.TryParse(http.Request.Query["limit"].ToString(), out int value) ? value : 20;
}
