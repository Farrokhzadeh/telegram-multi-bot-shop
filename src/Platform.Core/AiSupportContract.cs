namespace Platform.Core;

public sealed record AiOrderContext(Guid OrderId, string ProductTitle, string PaymentStatus, string FulfillmentStatus);
public sealed record AiShopContext(int Version, Guid BrandId, string BrandName, string Language, bool HumanSupportAvailable, string? PublicSupportContact, AiOrderContext? Order);
public sealed record AiTurn(AiShopContext Context, string Question)
{
    public override string ToString() => "AiTurn [redacted]";
}
public sealed record AiAdvice(int Version, string Text, bool HumanRequested)
{
    public override string ToString() => "AiAdvice [redacted]";
}
public enum AiDisposition { Advice, HumanSupport }
public enum AiFallback { None, Disabled, Unavailable, InvalidContext, InvalidReply, Timeout, CircuitOpen, Busy, Requested }
public sealed record AiOptionalResult(AiDisposition Disposition, string? Text, AiFallback Fallback)
{
    public override string ToString() => "AiOptionalResult [redacted]";
}
