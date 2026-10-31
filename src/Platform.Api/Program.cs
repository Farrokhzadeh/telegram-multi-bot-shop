using Platform.Core;

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
await app.RunAsync();
return 0;
