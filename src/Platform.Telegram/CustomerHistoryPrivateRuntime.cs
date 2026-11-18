using System.Globalization;

namespace Platform.Telegram;

public sealed class CustomerHistoryPrivateRuntime(
    TelegramWebhookVerifier verifier, CustomerHistoryDeliveryPipeline delivery, string token, long allowedActor, string attemptDirectory)
{
    public async Task<CustomerHistoryDeliveryReceipt> HandleAsync(IReadOnlyList<string> headers, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        var update = verifier.Verify(headers, payload);
        if (allowedActor <= 0 || update.ActorId != allowedActor || update.UpdateId <= 0 || update.MessageId <= 0 || update.IsCallback ||
            !CustomerHistoryCommandRouter.TryParsePage(update.Value, out _) ||
            !token.StartsWith(update.Context.BotId.ToString(CultureInfo.InvariantCulture) + ":", StringComparison.Ordinal))
            throw new TelegramIngressRejectedException();
        if (!OperatingSystem.IsLinux() || !Path.IsPathFullyQualified(attemptDirectory) ||
            Path.GetFullPath(attemptDirectory) != attemptDirectory || Path.GetFileName(attemptDirectory) != "teleg-history-attempts" ||
            !Directory.Exists(attemptDirectory) || new DirectoryInfo(attemptDirectory).LinkTarget is not null ||
            (File.GetUnixFileMode(attemptDirectory) & (UnixFileMode)63) != 0)
            throw new TelegramIngressRejectedException();
        string path = Path.Combine(attemptDirectory, update.Context.BotId.ToString(CultureInfo.InvariantCulture) + "-" +
            update.UpdateId.ToString(CultureInfo.InvariantCulture) + ".attempt");
        try
        {
            await using var marker = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            });
            await marker.WriteAsync("Attempt consumed. Never automatically resend historical records."u8.ToArray(), ct);
            await marker.FlushAsync(ct);
            marker.Flush(true);
        }
        catch (IOException) { throw new TelegramIngressRejectedException(); }
        return await delivery.SendAsync(token, update, ct);
    }
}
