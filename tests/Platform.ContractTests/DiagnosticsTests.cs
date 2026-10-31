using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Platform.Api;
using Platform.Core;

namespace Platform.ContractTests;

public sealed class DiagnosticsTests
{
    [Fact]
    public async Task PrivateRequestAndExceptionDataNeverReachStructuredLogsOrResponse()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/private-payload";
        context.Request.QueryString = new QueryString("?token=private-payload");
        context.Request.Headers["Authorization"] = "private-payload";
        context.Request.Headers["X-Request-Id"] = "private-payload";
        context.Response.Body = new MemoryStream();
        var logs = new List<string>();
        await new DiagnosticsMiddleware(_ => throw new InvalidOperationException("private-payload"), logs.Add).InvokeAsync(context);
        Assert.Equal(500, context.Response.StatusCode);
        Assert.True(Guid.TryParseExact(context.Response.Headers["X-Request-Id"], "D", out var id));
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
        var response = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("private-payload", response);
        using var document = JsonDocument.Parse(response);
        Assert.Equal(id, document.RootElement.GetProperty("requestId").GetGuid());
        Assert.Single(logs);
        Assert.DoesNotContain("private-payload", logs[0]);
        Assert.DoesNotContain("InvalidOperationException", logs[0]);
        Assert.Contains(id.ToString(), logs[0]);
    }

    [Theory]
    [InlineData(ErrorKind.InvalidRequest, 400)]
    [InlineData(ErrorKind.Forbidden, 403)]
    [InlineData(ErrorKind.Conflict, 409)]
    [InlineData(ErrorKind.Unavailable, 503)]
    public async Task ExplicitSafeErrorsMapWithoutRawExceptionDetails(ErrorKind kind, int expected)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var logs = new List<string>();
        await new DiagnosticsMiddleware(_ => throw new SafeOperationException(kind), logs.Add).InvokeAsync(context);
        Assert.Equal(expected, context.Response.StatusCode);
        Assert.Single(logs);
        using var document = JsonDocument.Parse(logs[0]);
        Assert.Equal(expected, document.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task SuccessfulRequestsHaveIndependentServerGeneratedCorrelations()
    {
        var logs = new List<string>();
        var middleware = new DiagnosticsMiddleware(context =>
        {
            Assert.NotEqual(Guid.Empty, context.GetRequestCorrelation());
            context.Response.StatusCode = 204;
            return Task.CompletedTask;
        }, logs.Add);
        var a = new DefaultHttpContext();
        var b = new DefaultHttpContext();
        await middleware.InvokeAsync(a);
        await middleware.InvokeAsync(b);
        Assert.NotEqual(a.Response.Headers["X-Request-Id"], b.Response.Headers["X-Request-Id"]);
        Assert.Throws<InvalidOperationException>(() => a.GetRequestCorrelation());
        Assert.Equal(2, logs.Count);
    }

    [Fact]
    public async Task DisconnectedRequestsDoNotExposeCancellationDetails()
    {
        var context = new DefaultHttpContext { RequestAborted = new CancellationToken(true) };
        context.Response.Body = new MemoryStream();
        var logs = new List<string>();
        await new DiagnosticsMiddleware(_ => throw new OperationCanceledException("private-payload", context.RequestAborted), logs.Add).InvokeAsync(context);
        Assert.Single(logs);
        Assert.DoesNotContain("private-payload", logs[0]);
        Assert.Equal(0, context.Response.Body.Length);
    }

    [Fact]
    public async Task LoggingFailureDoesNotRepeatTheHandlerOrChangeBusinessResult()
    {
        int calls = 0;
        var context = new DefaultHttpContext();
        await new DiagnosticsMiddleware(_ => { calls++; return Task.CompletedTask; }, _ => throw new IOException("private-payload")).InvokeAsync(context);
        Assert.Equal(1, calls);
        Assert.Equal(200, context.Response.StatusCode);
    }
}
