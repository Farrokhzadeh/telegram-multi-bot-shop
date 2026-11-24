using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Platform.Persistence.Configurations;

internal static class EfValues
{
    internal static DateTime TransactionTime() => throw new InvalidOperationException("The transaction clock cannot run on the client.");
    internal static DateTime UtcNow() => throw new InvalidOperationException("The database clock cannot run on the client.");
    internal static ValueConverter<DateTime, string> UtcTime { get; } = new(
        value => SqliteSchema.UtcText(value),
        value => DateTime.ParseExact(value, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal));
}
