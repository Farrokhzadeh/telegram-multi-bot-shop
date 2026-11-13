using System.Text;
using System.Text.Json.Serialization;
using Platform.Persistence;
using Platform.Security;
using Platform.Telegram;
namespace Platform.Testing;

public sealed record CutoverTelegramInput(Guid Run, long Update, string Body, string Header) { public override string ToString() => "CutoverTelegramInput [redacted]"; }
public sealed record CutoverTelegramKey(Guid Run, string Secret) { public override string ToString() => "CutoverTelegramKey [redacted]"; }
[JsonSerializable(typeof(CutoverTelegramInput))]
[JsonSerializable(typeof(CutoverTelegramKey))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class CutoverTelegramJson : JsonSerializerContext { }
public static class CutoverTelegramRouter
{
    public static async Task<UpdateRouteResult> RouteAsync(DbSessionFactory sessions, BrandContext context, Guid member, Guid run, ReadOnlyMemory<byte> secret, CutoverTelegramInput input, CancellationToken ct = default)
    {
        if (run == Guid.Empty || input.Run != run || member == Guid.Empty || input.Update < 0 || input.Body is null || input.Header is null || input.Header.Length != 43 || Encoding.UTF8.GetByteCount(input.Body) > 4096 || context.BrandId != SyntheticSecretFiles.BrandA || context.BotId != 1001) throw new TelegramIngressRejectedException();
        using var verifier = new TelegramWebhookVerifier(context, secret.Span);
        var update = verifier.Verify([input.Header], Encoding.UTF8.GetBytes(input.Body));
        if (update.UpdateId != input.Update || update.ActorId != CheckoutFixture.Actor(0)) throw new TelegramIngressRejectedException();
        await using (var read = await sessions.BeginReadOnlyAsync(ct))
            _ = await BrandUserStore.RequireAsync(read, context, update.ActorId, member, ct);
        return await new UpdateRouter(sessions, (_, _, _) => throw new TelegramIngressRejectedException()).RouteAsync(update, ct);
    }
}
