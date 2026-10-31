using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Platform.Core;

namespace Platform.Api;

public static class DiagnosticsEndpoints
{
    public static void MapFoundationDiagnostics(this WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Text("qualification-only"));
        // No configuration, DB or dependency probe can promote this incomplete foundation to business-ready.
        app.MapGet("/health/ready", () => Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
    }

    public static Guid GetRequestCorrelation(this HttpContext context) =>
        DiagnosticsMiddleware.Correlation(context) ?? throw new InvalidOperationException("Request correlation is unavailable.");
}

public sealed class DiagnosticsMiddleware(RequestDelegate next, Action<string> sink)
{
    internal static Guid? Correlation(HttpContext context) => context.Features.Get<RequestCorrelation>()?.Id;

    public async Task InvokeAsync(HttpContext context)
    {
        Guid id = Guid.NewGuid();
        var kind = ErrorKind.None;
        bool aborted = false;
        context.Features.Set(new RequestCorrelation(id));
        if (!context.Response.HasStarted)
        {
            context.Response.Headers["X-Request-Id"] = id.ToString("D");
            context.Response.OnStarting(() =>
            {
                context.Response.Headers["X-Request-Id"] = id.ToString("D");
                return Task.CompletedTask;
            });
        }
        try
        {
            await next(context);
        }
        catch (Exception exception)
        {
            kind = exception is OperationCanceledException && !context.RequestAborted.IsCancellationRequested
                ? ErrorKind.Unavailable : ErrorPolicy.Classify(exception);
            if (context.Response.HasStarted || context.RequestAborted.IsCancellationRequested)
            {
                aborted = true;
                if (!context.Response.HasStarted) context.Response.StatusCode = ErrorPolicy.HttpStatus(kind);
                context.Abort();
            }
            else
            {
                context.Response.Clear();
                context.Response.Headers["X-Request-Id"] = id.ToString("D");
                context.Response.StatusCode = ErrorPolicy.HttpStatus(kind);
                context.Response.ContentType = "application/json";
                byte[] body = JsonSerializer.SerializeToUtf8Bytes(new ApiFailure(ErrorPolicy.Code(kind), id),
                    DiagnosticsJsonContext.Default.ApiFailure);
                try { await context.Response.Body.WriteAsync(body, context.RequestAborted); }
                catch (Exception) { aborted = true; context.Abort(); }
            }
        }
        finally
        {
            context.Features.Set<RequestCorrelation>(null);
            if (kind == ErrorKind.None) kind = ErrorPolicy.FromStatus(context.Response.StatusCode);
            SafeDiagnostics.WriteApiCompletion(sink, id, context.Response.StatusCode, kind, aborted);
        }
    }

    private sealed record RequestCorrelation(Guid Id);
}
