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
public sealed record SqliteStageButton(string Text, string Data);
public sealed record SqliteStageReply(bool Accepted, string Text = "", bool Edit = false,
    SqliteStageButton[]? Buttons = null, string? Ack = null, Guid? Product = null, long? Revision = null);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SqliteStageRequest))]
[JsonSerializable(typeof(SqliteStageAck))]
[JsonSerializable(typeof(SqliteStageReply))]
[JsonSerializable(typeof(AdminPage<AdminBrand>))]
[JsonSerializable(typeof(AdminPage<AdminProduct>))]
[JsonSerializable(typeof(BrandPolicyDetail))]
[JsonSerializable(typeof(ExchangeRateMetadata))]
[JsonSerializable(typeof(AdminPage<InventoryMetadata>))]
public partial class SqliteStageApiJson : JsonSerializerContext { }

public static class SqliteStagingApi
{
    public static async Task<int> RunAsync(CancellationToken ct = default)
    {
        SqliteStagingConfiguration config;
        SecretSnapshot secrets;
        byte[] privateKey;
        try
        {
            config = await SqliteStagingConfiguration.LoadAsync(ct);
            secrets = config.LoadSecrets(SecretService.Api);
            privateKey = config.ReadApiKey();
        }
        catch (Exception error) when (error is InvalidOperationException or UnauthorizedAccessException
            or IOException or ArgumentException)
        {
            Console.Error.WriteLine("The isolated SQLite API staging configuration is unavailable.");
            return HostBootstrap.ConfigurationRequiredExitCode;
        }
        using (secrets)
        {
            try
            {
                var ingress = new SqliteStagingIngress(config.Sessions);
                var catalog = new SqliteCatalogStore(config.Sessions, secrets);
                var screens = new SqliteCustomerScreens(config.Sessions);
                var checkout = new SqliteCheckoutStore(config.Sessions, secrets);
                var admin = new SqliteAdminCatalogStore(config.Sessions);
                var builder = WebApplication.CreateSlimBuilder();
                builder.WebHost.UseUrls("http://127.0.0.1:18081");
                await using var app = builder.Build();
                app.MapGet("/health/sqlite", async (HttpContext http) =>
                {
                    if (!Authorize(http, privateKey)) return Results.Unauthorized();
                    await ingress.NextOffsetAsync(config.Context, http.RequestAborted);
                    return Results.Text("sqlite:ready");
                });
                app.MapPost("/staging/update", async (HttpContext http) =>
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
                        await http.Response.WriteAsJsonAsync(reply, SqliteStageApiJson.Default.SqliteStageReply,
                            http.RequestAborted);
                    }
                    catch (Exception error) when (error is UnauthorizedAccessException or ArgumentException
                        or PricingRejectedException or FormatException or InvalidOperationException)
                    {
                        http.Response.StatusCode = 409;
                        await http.Response.WriteAsJsonAsync(new SqliteStageReply(false,
                            "This staging action is unavailable. Open /shop again."),
                            SqliteStageApiJson.Default.SqliteStageReply, http.RequestAborted);
                    }
                });
                app.MapPost("/staging/ack", async (HttpContext http) =>
                {
                    if (!Authorize(http, privateKey)) return Results.Unauthorized();
                    try
                    {
                        var request = await JsonSerializer.DeserializeAsync(http.Request.Body,
                            SqliteStageApiJson.Default.SqliteStageAck, http.RequestAborted);
                        if (request is null || request.User != config.Recipient || request.Anchor<=0 ||
                            request.Product==Guid.Empty || request.UpdateId<0 ||
                            await ingress.NextOffsetAsync(config.Context, http.RequestAborted)<=request.UpdateId)
                            throw new UnauthorizedAccessException();
                        _ = await ingress.UpdateFlowAsync(config.Context, config.SyntheticActor, "catalog",
                            request.Product, null, request.Anchor, request.Revision, http.RequestAborted);
                        return Results.Ok();
                    }
                    catch (Exception error) when (error is UnauthorizedAccessException or ArgumentException)
                    { return Results.Conflict(); }
                });
                app.MapGet("/admin/brands", async (HttpContext http) =>
                {
                    if (!Authorize(http, privateKey)) return Results.Unauthorized();
                    try
                    {
                        var actor = await AuthenticateAsync(http, config);
                        var page = await admin.BrandsAsync(actor, After(http), Limit(http), http.RequestAborted);
                        await http.Response.WriteAsJsonAsync(page, SqliteStageApiJson.Default.AdminPageAdminBrand,
                            http.RequestAborted);
                        return Results.Empty;
                    }
                    catch (Exception error) when (error is UnauthorizedAccessException or AdminAuthenticationException
                        or ArgumentException or FormatException) { return Results.Unauthorized(); }
                });
                app.MapGet("/admin/products", async (HttpContext http) =>
                {
                    if (!Authorize(http, privateKey)) return Results.Unauthorized();
                    try
                    {
                        var actor = await AuthenticateAsync(http, config);
                        var page = await admin.ProductsAsync(actor, After(http), Limit(http), http.RequestAborted);
                        await http.Response.WriteAsJsonAsync(page, SqliteStageApiJson.Default.AdminPageAdminProduct,
                            http.RequestAborted);
                        return Results.Empty;
                    }
                    catch (Exception error) when (error is UnauthorizedAccessException or AdminAuthenticationException
                        or ArgumentException or FormatException) { return Results.Unauthorized(); }
                });
                app.MapGet("/admin/brands/{brand:guid}/policy", async (HttpContext http, Guid brand) =>
                {
                    if (!Authorize(http, privateKey) || brand!=config.Context.BrandId) return Results.Unauthorized();
                    try
                    {
                        var actor = await AuthenticateAsync(http, config);
                        var detail = await admin.PolicyAsync(actor, config.Context, http.RequestAborted);
                        await http.Response.WriteAsJsonAsync(detail, SqliteStageApiJson.Default.BrandPolicyDetail,
                            http.RequestAborted);
                        return Results.Empty;
                    }
                    catch (Exception error) when (error is UnauthorizedAccessException or AdminAuthenticationException)
                    { return Results.Unauthorized(); }
                });
                app.MapGet("/admin/brands/{brand:guid}/fx", async (HttpContext http, Guid brand) =>
                {
                    if (!Authorize(http, privateKey) || brand!=config.Context.BrandId) return Results.Unauthorized();
                    try
                    {
                        var actor = await AuthenticateAsync(http, config);
                        var rate = await admin.FxAsync(actor, config.Context, http.RequestAborted);
                        await http.Response.WriteAsJsonAsync(rate, SqliteStageApiJson.Default.ExchangeRateMetadata,
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
                        await http.Response.WriteAsJsonAsync(page, SqliteStageApiJson.Default.AdminPageInventoryMetadata,
                            http.RequestAborted);
                        return Results.Empty;
                    }
                    catch (Exception error) when (error is UnauthorizedAccessException or AdminAuthenticationException
                        or ArgumentException or FormatException) { return Results.Unauthorized(); }
                });
                Console.WriteLine("Managed SQLite staging API is starting.");
                await app.RunAsync(ct);
                return 0;
            }
            finally { CryptographicOperations.ZeroMemory(privateKey); }
        }
    }

    private static async Task<SqliteStageReply> ProcessAsync(SqliteStageRequest request,
        SqliteStagingConfiguration config, SqliteCatalogStore catalog, SqliteCheckoutStore checkout, SqliteCustomerScreens screens,
        SqliteStagingIngress ingress, CancellationToken ct)
    {
        if (request.Kind is not ("message" or "callback") || request.Value.Length is <1 or >4096 ||
            !await ingress.AdmitAsync(config.Context, request.UpdateId, ct))
            return new(false);
        long actor=config.SyntheticActor;
        var context=config.Context;
        if (request.Kind=="message")
        {
            string command=request.Value.Split(' ',2)[0].Split('@',2)[0];
            if (command is "/start" or "/shop" or "/checkoutpilot" or "/menu")
            {
                Guid[] ids=await catalog.VisibleProductsAsync(context, actor, ct);
                if (ids.Length==0) return new(true,"No staging products are currently listed.");
                CatalogItem? item=await catalog.GetAsync(context,actor,ids[0],ct);
                if(item is null)return new(true,"The staging catalog is unavailable.");
                SqliteStagingFlow flow=await ingress.ReadFlowAsync(context,actor,ct);
                return new(true,"Akanti staging shop\n"+item.Presentation.Title+
                    "\nPrice: "+item.UnitUsdt.ToString(CultureInfo.InvariantCulture)+" USDT\nAvailable: "+
                    (item.LocalStock>0?"yes":"no"),false,
                    [new("Select product","item"),new("Wallet","wallet"),new("Orders","orders")],
                    "catalog",item.ProductId,flow.Revision);
            }
            if(command=="/wallet")return await WalletAsync(screens,config,ct);
            if(command is "/orders" or "/transactions" or "/deposits")
                return await ScreenAsync(screens,config,command,ct);
            if(command=="/support")
            {
                CustomerHome home=await screens.HomeAsync(context,actor,ct);
                return new(true,home.Contact is null?"Support contact is not configured.":"Support: @"+home.Contact);
            }
            if(command=="/language")
            {
                CustomerHome home=await screens.HomeAsync(context,actor,ct);
                await screens.SetLanguageAsync(context,actor,home.Language=="en"?"fa":"en",ct);
                return new(true,"Staging language updated.");
            }
            return new(false);
        }
        if(request.Anchor is not >0)return new(false);
        string choice=request.Value.StartsWith("ak:",StringComparison.Ordinal)?request.Value[3..]:"";
        if(choice=="wallet")return await WalletAsync(screens,config,ct);
        if(choice=="orders")return await ScreenAsync(screens,config,"/orders",ct);
        SqliteStagingFlow state=await ingress.ReadFlowAsync(context,actor,ct);
        if(state.Anchor!=request.Anchor || state.Product is not Guid product) return new(false);
        if(choice=="item" && state.State=="catalog")
        {
            CatalogItem? item=await catalog.GetAsync(context,actor,product,ct);
            if(item is null)return new(true,"Product is unavailable.",true);
            int max=Math.Min(5,item.Maximum);
            var quantities=Enumerable.Range(item.Minimum,Math.Max(0,max-item.Minimum+1))
                .Select(n=>new SqliteStageButton(n.ToString(CultureInfo.InvariantCulture),"qty:"+n)).ToArray();
            if(quantities.Length==0)return new(true,"No staging stock is available.",true);
            _=await ingress.UpdateFlowAsync(context,actor,"quantity",product,null,request.Anchor,
                state.Revision,ct);
            return new(true,"Choose quantity for "+item.Presentation.Title,true,quantities);
        }
        if(choice.StartsWith("qty:",StringComparison.Ordinal) && state.State=="quantity" &&
            int.TryParse(choice.AsSpan(4),NumberStyles.None,CultureInfo.InvariantCulture,out int quantity) &&
            quantity is >=1 and <=5)
        {
            CheckoutQuote quote=await catalog.QuoteAsync(context,actor,
                new(product,quantity,CheckoutPayment.Wallet),ct);
            Guid order=Guid.CreateVersion7();
            _=await ingress.UpdateFlowAsync(context,actor,"payment",product,order,request.Anchor,
                state.Revision,ct);
            return new(true,QuoteText(quote),true,
                [new("Pay from test wallet","pay:"+order.ToString("N")),new("Cancel","cancel")]);
        }
        if(choice=="cancel" && state.State=="payment")
        {
            _=await ingress.UpdateFlowAsync(context,actor,"idle",null,null,null,state.Revision,ct);
            return new(true,"Staging checkout cancelled.",true);
        }
        if(choice.StartsWith("pay:",StringComparison.Ordinal) && state.State=="payment" &&
            Guid.TryParseExact(choice[4..],"N",out var orderId) && state.Order==orderId)
        {
            if(request.MessageText is not string confirmed || !confirmed.StartsWith("Staging quote\nQuantity: ",StringComparison.Ordinal))
                throw new UnauthorizedAccessException("The confirmed quote is unavailable.");
            string count=confirmed.Split('\n',StringSplitOptions.None).ElementAtOrDefault(1)??"";
            if(!int.TryParse(count.AsSpan("Quantity: ".Length),NumberStyles.None,CultureInfo.InvariantCulture,out int quantity) ||
                quantity is <1 or >5) throw new UnauthorizedAccessException();
            var prepared=await catalog.PrepareAsync(context,actor,
                new(product,quantity,CheckoutPayment.Wallet),ct);
            if(confirmed!=QuoteText(prepared.Quote))
                return new(true,"The staging quote changed. Open /shop to review the new price.",true);
            LocalCheckoutRequest requestToBuy=await catalog.RequireLocalAsync(context,actor,prepared,orderId,ct);
            CheckoutResult result=await checkout.PurchaseLocalAsync(context,requestToBuy.PolicyRevision,actor,
                requestToBuy,ct);
            if(result.Outcome is CheckoutOutcome.Purchased or CheckoutOutcome.Replayed)
            {
                _=await ingress.UpdateFlowAsync(context,actor,"idle",null,null,null,state.Revision,ct);
                return new(true,"Staging wallet order accepted. Delivery is queued.",true);
            }
            return new(true,"Checkout was not accepted: "+result.Outcome+". Open /shop to retry.",true);
        }
        return new(false);
    }

    private static string QuoteText(CheckoutQuote quote)=>
        "Staging quote\nQuantity: "+quote.Terms.Quantity.ToString(CultureInfo.InvariantCulture)+
        "\nTotal: "+quote.Payable.Amount.ToString(CultureInfo.InvariantCulture)+
        " "+quote.Payable.Currency+"\nProduct: "+quote.Terms.Product.Title;

    private static async Task<SqliteStageReply> WalletAsync(SqliteCustomerScreens screens,
        SqliteStagingConfiguration config,CancellationToken ct)
    {
        var home=await screens.HomeAsync(config.Context,config.SyntheticActor,ct);
        return new(true,"Staging wallet balance: "+home.Balance.ToString(CultureInfo.InvariantCulture)+
            " USD\nOrders: "+home.Orders.ToString(CultureInfo.InvariantCulture));
    }

    private static async Task<SqliteStageReply> ScreenAsync(SqliteCustomerScreens screens,
        SqliteStagingConfiguration config,string command,CancellationToken ct)
    {
        CustomerScreenList kind=command switch
        {
            "/orders"=>CustomerScreenList.Orders,
            "/transactions"=>CustomerScreenList.Transactions,
            "/deposits"=>CustomerScreenList.Deposits,
            _=>throw new UnauthorizedAccessException()
        };
        var result=await screens.ListAsync(config.Context,config.SyntheticActor,kind,"all",0,ct);
        var text=(result.Entries.Count==0?"No entries.":string.Join("\n",result.Entries.Select(x=>
            x.Title+" — "+x.Status+" ("+x.Id.ToString("N")[..12]+")")))+
            (result.HasNext?"\nMore entries available in the admin console.":"");
        return new(true,text);
    }

    private static bool Authorize(HttpContext http,byte[] key)
    {
        string raw=http.Request.Headers["X-Staging-Key"].ToString();
        if(raw.Length!=64 || !raw.All(c=>char.IsAsciiHexDigit(c)))return false;
        byte[] presented=Convert.FromHexString(raw);
        try{return CryptographicOperations.FixedTimeEquals(key,presented);}
        finally{CryptographicOperations.ZeroMemory(presented);}
    }

    private static async Task<AuthenticatedAdmin> AuthenticateAsync(HttpContext http,SqliteStagingConfiguration config)
    {
        string authorization=http.Request.Headers.Authorization.ToString();
        if(!authorization.StartsWith("Bearer ",StringComparison.Ordinal) || authorization.Length>120)
            throw new AdminAuthenticationException();
        using var token=AdminToken.Parse(authorization[7..]);
        return await SqliteAdminSessionStore.AuthenticateAsync(config.Sessions,token,http.RequestAborted);
    }
    private static Guid? After(HttpContext http)=>Guid.TryParse(http.Request.Query["after"].ToString(),out var id)?id:null;
    private static int Limit(HttpContext http)=>int.TryParse(http.Request.Query["limit"].ToString(),out int value)?value:20;
}
