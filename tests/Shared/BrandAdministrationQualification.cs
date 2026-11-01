using Platform.Persistence;
using Platform.Security;

namespace Platform.Testing;

public static class BrandAdministrationQualification
{
    public static async Task RunAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: ct);
        var admin = await f.AdminAsync(ct);
        var store = new BrandAdministrationStore(f.Sessions, f.Secrets);
        using var token = BotToken.Parse("1001:" + new string('s', 35));
        Guid rotation = Guid.NewGuid();
        int checks = 0;
        if (await store.RotateTokenAsync(admin, f.Context(0), token, 0, rotation, Verify, ct) != 1 ||
            await store.RotateTokenAsync(admin, f.Context(0), token, 0, rotation, Verify, ct) != 1 || checks != 1) throw new InvalidOperationException("Token lifecycle replay failed.");
        await using (var read = await f.Sessions.BeginAsync(ct))
        {
            var saved = await read.QuerySingleAsync(new DbStatement("SELECT bot_token_ciphertext,bot_token_key_id FROM shop.brands WHERE id=$1", DbValue.Uuid(f.Brands[0])), r => (Bytes: r.GetBinary(0), Key: r.GetString(1)), ct);
            using var opened = new BotTokenVault(f.Secrets).OpenForTrustedTransport(f.Context(0), saved.Key, saved.Bytes);
            if (!opened.ForTrustedTransport().Span.SequenceEqual(token.ForTrustedTransport().Span) || opened.ToString().Contains(new string('s', 35), StringComparison.Ordinal)) throw new InvalidOperationException("Token protection failed.");
        }
        var channels = new[] { new RequiredChannel(-10001, "Public required channel", "https://t.me/public_channel") };
        Guid update = Guid.NewGuid();
        if (await store.SetChannelsAsync(admin, f.Context(0), channels, 1, update, Eligible, ct) != 2 ||
            await store.SetChannelsAsync(admin, f.Context(0), channels, 1, update, Eligible, ct) != 2) throw new InvalidOperationException("Channel replay failed.");
        if (await f.CountAsync("SELECT count(*) FROM shop.brand_channels WHERE active AND required", ct) != 1 ||
            await f.CountAsync("SELECT count(*) FROM shop.audit_events WHERE entity_kind='brand' AND previous_revision IS NOT NULL", ct) != 2) throw new InvalidOperationException("Configuration audit failed.");
        Task<VerifiedBotProfile> Verify(BotToken input, CancellationToken cancellation) { checks++; return Task.FromResult(new VerifiedBotProfile(input.BotId, "public_test_bot")); }
        static Task<bool> Eligible(BrandContext context, IReadOnlyList<RequiredChannel> list, CancellationToken cancellation) => Task.FromResult(true);
    }
}
