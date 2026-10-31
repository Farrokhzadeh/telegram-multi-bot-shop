using System.Diagnostics;
using Platform.Persistence;
using Platform.Security;

namespace Platform.Telegram;

public enum ChannelMembership { Unknown, Left, Kicked, Member, Administrator, Creator, RestrictedMember }
public sealed class ChannelAccess
{
    private readonly BrandPolicySnapshot policy;
    private readonly long checkedAt = Stopwatch.GetTimestamp();
    private int used;
    internal ChannelAccess(BrandPolicySnapshot policy) => this.policy = policy;
    public Task RequireCurrentAsync(DbSession session, BrandContext context, long verifiedActorId, CancellationToken ct = default) =>
        BrandPolicyStore.RequireCurrentAsync(session, context, verifiedActorId, policy,
            Interlocked.Exchange(ref used, 1) == 0 && Stopwatch.GetElapsedTime(checkedAt) <= TimeSpan.FromSeconds(30), ct);
    public override string ToString() => "ChannelAccess [redacted]";
}

public static class BrandPolicy
{
    public static async Task<ChannelAccess?> CheckChannelsAsync(BrandPolicySnapshot policy, long verifiedActorId,
        Func<long, long, CancellationToken, Task<ChannelMembership>> check, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(check);
        ct.ThrowIfCancellationRequested();
        if (verifiedActorId <= 0 || verifiedActorId != policy.VerifiedActorId) throw new ArgumentOutOfRangeException(nameof(verifiedActorId));
        if (policy.EnforceChannels && !policy.VerifiedStaff)
        {
            foreach (RequiredChannel channel in policy.Channels)
            {
                ChannelMembership membership;
                try { membership = await check(channel.ChatId, verifiedActorId, ct); }
                catch (Exception error) when (error is HttpRequestException or TimeoutException) { return null; }
                if (membership is not (ChannelMembership.Member or ChannelMembership.Administrator or ChannelMembership.Creator or ChannelMembership.RestrictedMember)) return null;
            }
        }
        return new ChannelAccess(policy);
    }
}
