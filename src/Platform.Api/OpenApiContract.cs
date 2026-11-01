using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Platform.Core;
using Platform.Persistence;

namespace Platform.Api;

public sealed record ApiRoute(string Method, string Path, JsonTypeInfo? Request, JsonTypeInfo? Response, string[] Query);
public sealed record ApiInformation(string Title, string Version);
public sealed record ApiSecurityScheme(string Type, string Scheme);
public sealed record ApiSchema(string? Type = null, string? Format = null, bool? Nullable = null, bool? WriteOnly = null, bool? AdditionalProperties = null,
    Dictionary<string, ApiSchema>? Properties = null, string[]? Required = null, ApiSchema? Items = null, [property: JsonPropertyName("$ref")] string? Reference = null);
public sealed record ApiMedia(ApiSchema Schema);
public sealed record ApiResponse(string Description, Dictionary<string, ApiMedia>? Content = null);
public sealed record ApiBody(bool Required, Dictionary<string, ApiMedia> Content);
public sealed record ApiParameter(string Name, string In, bool Required, ApiSchema Schema);
public sealed record ApiOperation(string OperationId, Dictionary<string, string[]>[] Security, ApiParameter[] Parameters, ApiBody? RequestBody, Dictionary<string, ApiResponse> Responses);
public sealed record ApiComponents(Dictionary<string, ApiSecurityScheme> SecuritySchemes, Dictionary<string, ApiSchema> Schemas);
public sealed record ApiDocument(string Openapi, ApiInformation Info, SortedDictionary<string, SortedDictionary<string, ApiOperation>> Paths, ApiComponents Components);
public static partial class OpenApiContract
{
    public static ApiRoute[] Routes => [
        new("GET", "/v1/admin/payments/{id}", null, PaymentApiJsonContext.Default.PaymentStatusView, ["brandId!"]),
        new("GET", "/v1/admin/manual-deposits/{id}", null, PaymentApiJsonContext.Default.PaymentStatusView, ["brandId!"]),
        new("GET", "/v1/admin/brands/{brand}", null, BrandAdminJsonContext.Default.BrandConfiguration, []),
        new("PUT", "/v1/admin/brands/{brand}/bot-token", BrandAdminJsonContext.Default.BotTokenChange, BrandAdminJsonContext.Default.ConfigurationSaved, []),
        new("PUT", "/v1/admin/brands/{brand}/required-channels", BrandAdminJsonContext.Default.RequiredChannelsChange, BrandAdminJsonContext.Default.ConfigurationSaved, []),
        new("PATCH", "/v1/admin/brands/{brand}", CatalogApiJsonContext.Default.AdminChangeBrandPolicyEdit, BrandAdminJsonContext.Default.ConfigurationSaved, []),
        new("PUT", "/v1/admin/brands/{brand}/channel-enforcement", CatalogApiJsonContext.Default.ChannelEnforcementChange, BrandAdminJsonContext.Default.ConfigurationSaved, []),
        new("PUT", "/v1/admin/brands/{brand}/bank-destination", OrderApiJsonContext.Default.BankDestinationCommand, null, []),
        new("GET", "/v1/admin/brands", null, CatalogApiJsonContext.Default.AdminPageAdminBrand, ["after", "limit"]),
        new("GET", "/v1/admin/brands/{brand}/policy", null, CatalogApiJsonContext.Default.BrandPolicyDetail, []),
        new("GET", "/v1/admin/brands/{brand}/exchange-rate", null, CatalogApiJsonContext.Default.ExchangeRateMetadata, []),
        new("GET", "/v1/admin/products", null, CatalogApiJsonContext.Default.AdminPageAdminProduct, ["after", "limit"]),
        new("PUT", "/v1/admin/products/{id}", CatalogApiJsonContext.Default.AdminChangeMasterProductEdit, BrandAdminJsonContext.Default.ConfigurationSaved, []),
        new("PUT", "/v1/admin/products/{id}/brands/{brand}", CatalogApiJsonContext.Default.AdminChangeListingEdit, BrandAdminJsonContext.Default.ConfigurationSaved, []),
        new("GET", "/v1/admin/inventory", null, CatalogApiJsonContext.Default.AdminPageInventoryMetadata, ["productId!", "after", "limit"]),
        new("POST", "/v1/admin/inventory/import", CatalogApiJsonContext.Default.StockImport, null, []),
        new("GET", "/v1/admin/orders", null, OrderApiJsonContext.Default.AdminPageAdminOrder, ["brandId", "memberId", "orderId", "after", "limit"]),
        new("POST", "/v1/admin/wallet-adjustments", OrderApiJsonContext.Default.WalletCommand, OrderApiJsonContext.Default.AdminIdentity, []),
        new("POST", "/v1/admin/orders/{id}/resolution", OrderApiJsonContext.Default.ResolutionCommand, null, []),
        new("POST", "/v1/admin/manual-deposits/{id}/decision", OrderApiJsonContext.Default.ReceiptDecision, null, []),
        new("PATCH", "/v1/admin/coupons/{id}", OrderApiJsonContext.Default.CouponEditCommand, BrandAdminJsonContext.Default.ConfigurationSaved, []),
        new("GET", "/v1/admin/audit", null, OperationsJsonContext.Default.AdminPageAuditMetadata, ["brandId", "after", "limit"]),
        new("GET", "/v1/admin/operations", null, OperationsJsonContext.Default.WorkBacklogArray, ["brandId"]),
        new("GET", "/v1/admin/campaigns", null, OperationsJsonContext.Default.AdminPageCampaignMetadata, ["brandId", "after", "limit"]),
        new("POST", "/v1/admin/campaigns", CampaignJsonContext.Default.CampaignLaunch, OrderApiJsonContext.Default.AdminIdentity, []),
        new("POST", "/v1/admin/campaign-batches/{id}/send", CampaignJsonContext.Default.CampaignTransition, null, []),
        new("POST", "/v1/admin/campaign-batches/{id}/cancel", CampaignJsonContext.Default.CampaignTransition, null, []),
        new("GET", "/v1/admin/coupons", null, OperationsJsonContext.Default.AdminPageCouponMetadata, ["brandId!", "after", "limit"]),
        new("GET", "/v1/admin/coupons/{id}/revisions", null, OperationsJsonContext.Default.CouponRevisionPage, ["brandId!", "afterRevision", "limit"])
    ];
    public static ApiDocument Generate(IEndpointRouteBuilder endpoints)
    {
        var actual = endpoints.DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().Where(e => e.RoutePattern.RawText?.StartsWith("/v1/admin/", StringComparison.Ordinal) == true).ToArray();
        var paths = new SortedDictionary<string, SortedDictionary<string, ApiOperation>>(StringComparer.Ordinal);
        var schemas = new Dictionary<string, ApiSchema>(StringComparer.Ordinal);
        foreach (var endpoint in actual)
        {
            if (endpoint.Metadata.GetMetadata<AdministratorEndpointMetadata>() is null) throw new InvalidOperationException("Unprotected administrator contract route.");
            string path = RouteConstraint().Replace(endpoint.RoutePattern.RawText!, "}");
            foreach (string method in endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods)
            {
                ApiRoute? entry = Routes.SingleOrDefault(r => r.Path == path && r.Method == method);
                if (entry is null) throw new InvalidOperationException("Administrator route is missing its frozen contract.");
                var parameters = RouteParameter().Matches(path).Select(m => new ApiParameter(m.Groups[1].Value, "path", true, new("string", "uuid"))).Concat(entry.Query.Select(q =>
                    new ApiParameter(q.TrimEnd('!'), "query", q.EndsWith('!'), q.StartsWith("limit", StringComparison.Ordinal) || q.StartsWith("afterRevision", StringComparison.Ordinal) ? new("integer", "int64") : new("string", "uuid")))).ToArray();
                var responses = new Dictionary<string, ApiResponse>(StringComparer.Ordinal) { [entry.Response is null ? "204" : "200"] = new("Committed or current result", entry.Response is null ? null : Content(Schema(entry.Response, schemas))) };
                foreach (string status in new[] { "400", "401", "403", "404", "409", "413", "415", "408", "500", "503" }) responses[status] = new("Redacted error", Content(Schema(DiagnosticsJsonContext.Default.ApiFailure, schemas)));
                var operation = new ApiOperation(method.ToLowerInvariant() + "_" + path.Replace('/', '_').Replace("{", "", StringComparison.Ordinal).Replace("}", "", StringComparison.Ordinal),
                    [new() { ["adminBearer"] = [] }], parameters, entry.Request is null ? null : new(true, Content(Schema(entry.Request, schemas))), responses);
                if (!paths.TryGetValue(path, out var methods)) paths[path] = methods = new(StringComparer.Ordinal);
                methods.Add(method.ToLowerInvariant(), operation);
            }
        }
        return new("3.0.3", new("Multi-brand shop administration", "1.0.0-offline"), paths, new(new() { ["adminBearer"] = new("http", "bearer") }, schemas));
    }
    private static Dictionary<string, ApiMedia> Content(ApiSchema schema) => new() { ["application/json"] = new(schema) };
    private static ApiSchema Schema(JsonTypeInfo info, Dictionary<string, ApiSchema> schemas)
    {
        Type type = info.Type;
        if (Nullable.GetUnderlyingType(type) is Type underlying) return Schema(info.Options.GetTypeInfo(underlying), schemas) with { Nullable = true };
        if (type == typeof(string)) return new("string");
        if (type == typeof(Guid)) return new("string", "uuid");
        if (type == typeof(DateTime)) return new("string", "date-time");
        if (type == typeof(bool)) return new("boolean");
        if (type.IsEnum || type == typeof(int) || type == typeof(long)) return new("integer", "int64");
        if (type == typeof(decimal)) return new("number");
        if (type.IsArray) return new("array", Items: Schema(info.Options.GetTypeInfo(type.GetElementType()!), schemas));
        string name = Name(type);
        if (!schemas.ContainsKey(name))
        {
            schemas[name] = new("object");
            var properties = new Dictionary<string, ApiSchema>(StringComparer.Ordinal);
            foreach (var property in info.Properties)
                properties[property.Name] = Schema(info.Options.GetTypeInfo(property.PropertyType), schemas) with
                { Nullable = property.IsGetNullable, WriteOnly = property.Name is "Token" or "Number" or "Holder" or "Credential" ? true : null };
            string[] required = info.Properties.Where(p => p.IsRequired || (info.Options.RespectRequiredConstructorParameters && p.AssociatedParameter is { HasDefaultValue: false })).Select(p => p.Name).ToArray();
            schemas[name] = new("object", AdditionalProperties: false, Properties: properties, Required: required.Length == 0 ? null : required);
        }
        return new(Reference: "#/components/schemas/" + name);
    }
    private static string Name(Type type) => type.IsGenericType ? type.Name.Split('`')[0] + string.Concat(type.GenericTypeArguments.Select(Name)) : type.Name;
    [GeneratedRegex(@":[^}]+}")]
    private static partial Regex RouteConstraint();
    [GeneratedRegex(@"{([^}]+)}")]
    private static partial Regex RouteParameter();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ApiDocument))]
internal sealed partial class ContractJsonContext : JsonSerializerContext;
