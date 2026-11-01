using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using Platform.Core;
using Platform.Persistence;

namespace Platform.Api;

internal static class AdminHttp
{
    internal static async Task<IResult> HandleAsync(HttpContext http, Func<Task<IResult>> action)
    {
        try
        {
            IResult result = await action();
            return result is IStatusCodeHttpResult { StatusCode: >= 400 } error ? Failure(http, error.StatusCode.Value) : result;
        }
        catch (Exception error) when (error is JsonException or ArgumentException) { throw new SafeOperationException(ErrorKind.InvalidRequest); }
        catch (Exception error) when (error is FormatException or InvalidOperationException) { throw new SafeOperationException(ErrorKind.Conflict); }
        catch (DbOperationException error) when (error.Failure.Kind is DbFailureKind.InvalidData or DbFailureKind.Constraint) { throw new SafeOperationException(ErrorKind.Conflict); }
        catch (BadHttpRequestException error) { return Failure(http, error.StatusCode); }
    }
    internal static IResult Failure(HttpContext http, int status)
    {
        Guid id = DiagnosticsMiddleware.Correlation(http) ?? Guid.NewGuid();
        http.Response.Headers["X-Request-Id"] = id.ToString("D");
        return Results.Json(new ApiFailure(ErrorPolicy.Code(ErrorPolicy.FromStatus(status)), id), DiagnosticsJsonContext.Default.ApiFailure, statusCode: status);
    }
    internal static async Task<T> ReadAsync<T>(HttpContext http, JsonTypeInfo<T> type)
    {
        if (!http.Request.HasJsonContentType()) throw new BadHttpRequestException("JSON content is required.", StatusCodes.Status415UnsupportedMediaType);
        byte[] bytes = new byte[1048577];
        try
        {
            int length = 0, count;
            while (length < bytes.Length && (count = await http.Request.Body.ReadAsync(bytes.AsMemory(length), http.RequestAborted)) != 0) length += count;
            if (length == bytes.Length) throw new BadHttpRequestException("Request is too large.", StatusCodes.Status413PayloadTooLarge);
            return JsonSerializer.Deserialize(bytes.AsSpan(0, length), type) ?? throw new ArgumentException("Request is required.");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
