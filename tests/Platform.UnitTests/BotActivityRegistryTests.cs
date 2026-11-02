using Platform.Core;

namespace Platform.UnitTests;

public sealed class BotActivityRegistryTests
{
    [Fact]
    public void StableVersionOneHasOnlyTheElevenCanonicalActions()
    {
        Assert.Equal(1, BotActivityRegistry.Version);
        var actions = Enum.GetValues<BotActivityAction>(); Assert.Equal(11, actions.Length);
        Assert.Equal(actions.Length, actions.Select(a => BotActivityRegistry.Get(a).Code).Distinct(StringComparer.Ordinal).Count());
        foreach (var action in actions) Assert.Equal(action, BotActivityRegistry.FromCode(BotActivityRegistry.Get(action).Code).Action);
    }
    [Fact]
    public void RawOrUnknownCodesAndInvalidLinksHaveNoRegistryAuthority()
    {
        Assert.Throws<ArgumentException>(() => BotActivityRegistry.FromCode("public raw message or coupon text"));
        Assert.Throws<ArgumentException>(() => BotActivityRegistry.Get((BotActivityAction)999));
        Assert.Throws<ArgumentException>(() => BotActivityRegistry.Validate(BotActivityAction.QuantityEnter, BotActivitySource.Flow, BotActivityOutcome.Accepted, new(Product: Guid.NewGuid())));
        Assert.Throws<ArgumentException>(() => BotActivityRegistry.Validate(BotActivityAction.SupportReply, BotActivitySource.Flow, BotActivityOutcome.Accepted, new()));
        Assert.Throws<ArgumentException>(() => BotActivityRegistry.Validate(BotActivityAction.CheckoutOpen, BotActivitySource.Callback, BotActivityOutcome.Accepted, new(Product: Guid.Empty)));
        Assert.Throws<ArgumentException>(() => BotActivityRegistry.Validate(BotActivityAction.Start, (BotActivitySource)999, BotActivityOutcome.Accepted, new()));
        Assert.Throws<ArgumentOutOfRangeException>(() => BotActivityRegistry.TelegramIdentity(-1));
        Assert.Equal("telegram:123", BotActivityRegistry.TelegramIdentity(123));
    }
}
