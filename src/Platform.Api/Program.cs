using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Platform.Api;
using Platform.Core;
using Platform.Security;

if (args is not ["--serve-qualification"])
{
    return HostBootstrap.Run(args, "Api");
}

var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
builder.Logging.ClearProviders();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, PlatformJsonContext.Default));
var app = builder.Build();
app.Urls.Clear();
app.Urls.Add("http://127.0.0.1:18080");
app.UseRouting();
// Public synthetic identities exist only in this explicit loopback qualification host.
// This fixture is not a production credential handler or a verified Telegram ingress.
var qualificationBrands = new BotBrandRegistry([
    new("qualification-bot", "a", Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), 101,
        Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"), 1, true),
    new("qualification-bot", "b", Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), 102,
        Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"), 1, true)]);
app.Use(async (context, next) =>
{
    var subject = context.Request.Headers["X-Qualification-Bot"].ToString();
    if (subject is "a" or "b")
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, subject)], "qualification-bot"));
        context.Features.Set<IAuthenticateResultFeature>(new QualificationTicketFeature
        {
            AuthenticateResult = AuthenticateResult.Success(new AuthenticationTicket(principal, "qualification-bot"))
        });
    }
    await next(context);
});
app.UseMiddleware<BrandContextMiddleware>(qualificationBrands);
app.MapGet("/health/live", () => Results.Text("qualification-only"));
app.MapGet("/health/ready", () => Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
app.MapGet("/qualification", static IResult (HttpContext context) =>
{
    if (!string.Equals(context.Request.Headers["X-Qualification-Token"],
        "public-synthetic-probe", StringComparison.Ordinal))
    {
        return Results.Unauthorized();
    }

    return Results.Json(new QualificationStatus("Api", "synthetic-qualification", false),
        PlatformJsonContext.Default.QualificationStatus);
});
app.MapMethods("/qualification/customer-context", ["GET", "POST"],
    static (HttpContext context) => Results.Text(context.GetRequiredBrandContext().BrandId.ToString()))
    .RequireCustomerBrandContext();
app.MapGet("/qualification/customer-context/{brandId}",
    static (HttpContext context) => Results.Text(context.GetRequiredBrandContext().BrandId.ToString()))
    .RequireCustomerBrandContext();
await app.RunAsync();
return 0;

internal sealed class QualificationTicketFeature : IAuthenticateResultFeature
{
    public AuthenticateResult? AuthenticateResult { get; set; }
}
