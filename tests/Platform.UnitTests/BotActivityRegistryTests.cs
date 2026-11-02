using Platform.Core;

namespace Platform.UnitTests;

public sealed class BotActivityRegistryTests
{
    [Fact]
    public void VersionSixPreservesOriginalActionsAndAddsTypedFlowAbandonment()
    {
        Assert.Equal(6, BotActivityRegistry.Version);
        var actions = Enum.GetValues<BotActivityAction>(); Assert.Equal(26, actions.Length);
        Assert.Equal("policy_check", BotActivityRegistry.Get((BotActivityAction)10).Code);
        Assert.Equal(11, (int)BotActivityAction.CheckoutCreated);
        Assert.Equal(12, (int)BotActivityAction.FlowExpired);
        Assert.Equal(13, (int)BotActivityAction.QuantityRejected);
        Assert.Equal(14, (int)BotActivityAction.CheckoutFunded);
        Assert.Equal(15, (int)BotActivityAction.CheckoutExpired);
        Assert.Equal(16, (int)BotActivityAction.CheckoutReplaced);
        Assert.Equal(17, (int)BotActivityAction.CheckoutRejected);
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
        Assert.Throws<ArgumentException>(() => BotActivityRegistry.Validate(BotActivityAction.CheckoutCreated, BotActivitySource.Flow, BotActivityOutcome.Accepted, new(Product: Guid.NewGuid(), Quantity: 1)));
        Assert.Throws<ArgumentException>(() => BotActivityRegistry.Validate(BotActivityAction.FlowExpired, BotActivitySource.System, BotActivityOutcome.Cancelled, new()));
        Assert.Throws<ArgumentException>(() => BotActivityRegistry.Validate(BotActivityAction.QuantityRejected, BotActivitySource.Flow, BotActivityOutcome.Accepted, new(Product: Guid.NewGuid(), FlowRevision: 1)));
        Assert.Throws<ArgumentException>(() => BotActivityRegistry.Validate(BotActivityAction.CheckoutFunded, BotActivitySource.Callback, BotActivityOutcome.Accepted, new(Product: Guid.NewGuid(), Order: Guid.NewGuid(), Quantity: 1)));
        Assert.Throws<ArgumentException>(() => BotActivityRegistry.Validate(BotActivityAction.CheckoutExpired, BotActivitySource.System, BotActivityOutcome.Accepted, new(Product: Guid.NewGuid(), Order: Guid.NewGuid(), Quantity: 1)));
    }
}
