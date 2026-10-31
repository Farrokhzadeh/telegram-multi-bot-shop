using System.Text.Json;
using System.Text.Json.Serialization;

namespace Platform.Core;

public enum ErrorKind { None, InvalidRequest, Unauthenticated, Forbidden, NotFound, Conflict, Unavailable, Canceled, Internal }

public interface ISafeApiError { ErrorKind Kind { get; } }

public sealed class SafeOperationException : Exception, ISafeApiError
{
    public ErrorKind Kind { get; }
    public SafeOperationException(ErrorKind kind) : base("Operation rejected.")
    {
        if (kind is ErrorKind.None || !Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        Kind = kind;
    }
}

public static class ErrorPolicy
{
    public static ErrorKind Classify(Exception exception) => exception switch
    {
        ISafeApiError safe => safe.Kind,
        OperationCanceledException => ErrorKind.Canceled,
        UnauthorizedAccessException => ErrorKind.Forbidden,
        TimeoutException => ErrorKind.Unavailable,
        _ => ErrorKind.Internal
    };

    public static int HttpStatus(ErrorKind kind) => kind switch
    {
        ErrorKind.InvalidRequest => 400,
        ErrorKind.Unauthenticated => 401,
        ErrorKind.Forbidden => 403,
        ErrorKind.NotFound => 404,
        ErrorKind.Conflict => 409,
        ErrorKind.Unavailable => 503,
        ErrorKind.Canceled => 408,
        _ => 500
    };

    public static ErrorKind FromStatus(int status) => status switch
    {
        401 => ErrorKind.Unauthenticated,
        403 => ErrorKind.Forbidden,
        404 => ErrorKind.NotFound,
        409 => ErrorKind.Conflict,
        408 => ErrorKind.Canceled,
        429 or 503 => ErrorKind.Unavailable,
        >= 500 => ErrorKind.Internal,
        >= 400 => ErrorKind.InvalidRequest,
        _ => ErrorKind.None
    };

    public static string Code(ErrorKind kind) => kind switch
    {
        ErrorKind.None => "none",
        ErrorKind.InvalidRequest => "validation_failed",
        ErrorKind.Unauthenticated => "unauthenticated",
        ErrorKind.Forbidden => "forbidden",
        ErrorKind.NotFound => "not_found",
        ErrorKind.Conflict => "conflict",
        ErrorKind.Unavailable => "temporarily_unavailable",
        ErrorKind.Canceled => "request_canceled",
        _ => "internal_error"
    };
}

// Only server-generated correlation, allowlisted error code, status and fixed component enter this schema.
public sealed record DiagnosticEvent(string Component, Guid RequestId, int Status, string Code, bool Aborted);
public sealed record ApiFailure(string Code, Guid RequestId);

[JsonSerializable(typeof(DiagnosticEvent))]
[JsonSerializable(typeof(ApiFailure))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class DiagnosticsJsonContext : JsonSerializerContext { }

public static class SafeDiagnostics
{
    private static long dropped;
    public static long DroppedEvents => Interlocked.Read(ref dropped);
    public static void WriteApiCompletion(Action<string> sink, Guid requestId, int status, ErrorKind kind, bool aborted = false)
    {
        if (requestId == Guid.Empty || status is < 100 or > 599) throw new ArgumentException("Invalid diagnostic metadata.");
        var record = new DiagnosticEvent("Api", requestId, status, ErrorPolicy.Code(kind), aborted);
        var json = JsonSerializer.Serialize(record, DiagnosticsJsonContext.Default.DiagnosticEvent);
        // Diagnostic sink failure never retries business effects or prints a raw fallback exception.
        try { sink(json); }
        catch (Exception) { Interlocked.Increment(ref dropped); }
    }
}
