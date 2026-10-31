using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Platform.Api;
using Platform.Security;

namespace Platform.ContractTests;

public sealed class BrandContextMiddlewareTests
{
    private static readonly Guid BrandA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid BrandB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly BotBrandRegistry Registry = new([
        new("test-bot", "a", BrandA, 101, BrandA, 1, true),
        new("test-bot", "b", BrandB, 102, BrandB, 1, true)]);

    [Fact]
    public async Task BareUserAndFailedTicketHaveNoBrandAuthority()
    {
        var context = Request();
        context.Features.Set<IAuthenticateResultFeature>(null);
        Assert.True(context.User.Identity!.IsAuthenticated);
        await Reject(context, 401);
        context = Request();
        context.Features.Set<IAuthenticateResultFeature>(new TicketFeature { AuthenticateResult = AuthenticateResult.Fail("private-detail") });
        await Reject(context, 401);
        context = Request();
        context.Features.Set<IAuthenticateResultFeature>(new TicketFeature
        {
            AuthenticateResult = AuthenticateResult.Success(new AuthenticationTicket(context.User, "admin"))
        });
        await Reject(context, 401);
    }

    [Theory]
    [InlineData("Brand")]
    [InlineData("X-Brand-Id")]
    [InlineData("X_BotId")]
    [InlineData("tenant_id")]
    [InlineData("DeploymentId")]
    public async Task HeadersCannotSelectEvenTheSameBrand(string name)
    {
        var context = Request();
        context.Request.Headers[name] = BrandA.ToString();
        await Reject(context, 400);
    }

    [Theory]
    [InlineData("?brand_id=foreign")]
    [InlineData("?%62rand%49d=foreign")]
    [InlineData("?tenant-id=foreign&tenant-id=same")]
    [InlineData("?botId=102")]
    public async Task QuerySelectorsAreRejectedAfterDecoding(string query)
    {
        var context = Request();
        context.Request.QueryString = new QueryString(query);
        await Reject(context, 400);
    }

    [Fact]
    public async Task RouteSelectorsAreRejected()
    {
        var context = Request();
        context.Request.RouteValues["brandId"] = BrandB;
        await Reject(context, 400);
    }

    [Theory]
    [InlineData("{\"brand_id\":\"foreign\"}")]
    [InlineData("{\"items\":[{\"brandId\":\"foreign\"}]}")]
    [InlineData("{\"\\u0062rand_id\":\"foreign\"}")]
    [InlineData("{\"ok\":1,\"brand_id\":1,\"brand_id\":2}")]
    [InlineData("{\"bot_id\":102}")]
    [InlineData("{\"deployment_id\":\"foreign\"}")]
    [InlineData("{\"tenant\":\"foreign\"}")]
    public async Task JsonPropertySelectorsAreRejectedStructurally(string body) => await Reject(Request(body), 400);

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"text\":1,}")]
    public async Task MalformedOrNonObjectJsonIsRejected(string body) => await Reject(Request(body), 400);

    [Fact]
    public async Task DeepJsonAndOversizedStreamsAreBounded()
    {
        await Reject(Request("{\"x\":" + new string('[', 70) + "0" + new string(']', 70) + "}"), 400);
        var context = Request("{\"text\":\"" + new string('x', 1024) + "\"}");
        context.Request.ContentLength = null;
        await Reject(context, 413, maximumBodyBytes: 128);
        context = Request();
        context.Request.ContentLength = 129;
        await Reject(context, 413, maximumBodyBytes: 128);
        context = Request("{}");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        await Reject(context, 415);
    }

    [Fact]
    public async Task BodyBytesSurviveAndContextIsClearedAfterHandlerFailure()
    {
        const string body = "{ \"text\": \"brand_id=foreign; botId is ordinary text\", \"order_id\": 42 }";
        var context = Request(body);
        var original = context.Request.Body;
        var middleware = new BrandContextMiddleware(async current =>
        {
            Assert.Equal(BrandA, current.GetRequiredBrandContext().BrandId);
            using var reader = new StreamReader(current.Request.Body, leaveOpen: true);
            Assert.Equal(body, await reader.ReadToEndAsync());
            throw new InvalidOperationException("handler-failed");
        }, Registry);
        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));
        Assert.Same(original, context.Request.Body);
        Assert.Equal(0, original.Position);
        Assert.Throws<InvalidOperationException>(() => context.GetRequiredBrandContext());
    }

    [Fact]
    public async Task ConcurrentBrandsHaveIndependentRequestContexts()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var middleware = new BrandContextMiddleware(async current =>
        {
            var brand = current.GetRequiredBrandContext().BrandId;
            if (Interlocked.Increment(ref count) == 2) entered.SetResult();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(brand, current.GetRequiredBrandContext().BrandId);
            Assert.Equal(current.User.FindFirstValue(ClaimTypes.NameIdentifier) == "a" ? BrandA : BrandB, brand);
        }, Registry);
        var a = Request();
        var b = Request(subject: "b");
        await Task.WhenAll(middleware.InvokeAsync(a), middleware.InvokeAsync(b));
        Assert.Throws<InvalidOperationException>(() => a.GetRequiredBrandContext());
        Assert.Throws<InvalidOperationException>(() => b.GetRequiredBrandContext());
    }

    [Fact]
    public async Task UnmarkedEndpointsHaveNoImplicitBrand()
    {
        var context = Request();
        context.SetEndpoint(null);
        var called = false;
        await new BrandContextMiddleware(current =>
        {
            called = true;
            Assert.Throws<InvalidOperationException>(() => current.GetRequiredBrandContext());
            return Task.CompletedTask;
        }, Registry).InvokeAsync(context);
        Assert.True(called);
    }

    [Fact]
    public async Task NonSeekableBodyCanBeReadDownstreamAndCancellationDoesNotBindContext()
    {
        var context = Request("{\"text\":\"ok\"}");
        context.Request.Body = new NonSeekableStream(context.Request.Body);
        await new BrandContextMiddleware(async current =>
        {
            Assert.Equal(BrandA, current.GetRequiredBrandContext().BrandId);
            using var reader = new StreamReader(current.Request.Body, leaveOpen: true);
            Assert.Equal("{\"text\":\"ok\"}", await reader.ReadToEndAsync());
        }, Registry).InvokeAsync(context);
        context = Request("{}");
        context.RequestAborted = new CancellationToken(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BrandContextMiddleware(_ => Task.CompletedTask, Registry).InvokeAsync(context));
        Assert.Throws<InvalidOperationException>(() => context.GetRequiredBrandContext());
    }

    private static DefaultHttpContext Request(string? body = null, string subject = "a")
    {
        var context = new DefaultHttpContext();
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(CustomerBrandEndpointMetadata.Instance), "synthetic-customer"));
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, subject)], "test-bot"));
        context.Features.Set<IAuthenticateResultFeature>(new TicketFeature
        {
            AuthenticateResult = AuthenticateResult.Success(new AuthenticationTicket(context.User, "test-bot"))
        });
        if (body is not null)
        {
            context.Request.ContentType = "application/json";
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            context.Request.ContentLength = Encoding.UTF8.GetByteCount(body);
        }
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task Reject(DefaultHttpContext context, int status, int maximumBodyBytes = 1048576)
    {
        var called = false;
        await new BrandContextMiddleware(_ => { called = true; return Task.CompletedTask; }, Registry, maximumBodyBytes).InvokeAsync(context);
        Assert.False(called);
        Assert.Equal(status, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
        Assert.Throws<InvalidOperationException>(() => context.GetRequiredBrandContext());
    }

    private sealed class TicketFeature : IAuthenticateResultFeature
    {
        public AuthenticateResult? AuthenticateResult { get; set; }
    }

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
