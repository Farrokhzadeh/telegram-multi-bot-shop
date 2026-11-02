using System.Globalization;

namespace Platform.Core;

public enum BotActivityAction { Start, CatalogOpen, ProductOpen, QuantityEnter, CheckoutOpen, SupportOpen, SupportReply, Subscribe, Unsubscribe, DiscountsOpen, PolicyCheck, CheckoutCreated, FlowExpired }
public enum BotActivitySource { Command, Callback, Flow, System }
public enum BotActivityOutcome { Accepted, Rejected, Unavailable, Blocked, Cancelled, Duplicate }
public readonly record struct BotActivityDefinition(BotActivityAction Action, string EventType, string Code, bool ProductRequired = false, bool QuantityRequired = false, bool TicketRequired = false, bool OrderRequired = false, bool RevisionRequired = false);
public readonly record struct BotActivityContext(Guid? Product = null, Guid? Order = null, Guid? Ticket = null, int? Quantity = null, long? FlowRevision = null, long? TelegramUpdate = null);
public static class BotActivityRegistry
{
    public const int Version = 2;
    public static BotActivityDefinition Get(BotActivityAction action) => action switch
    {
        BotActivityAction.Start => new(action, "navigation", "start"),
        BotActivityAction.CatalogOpen => new(action, "navigation", "catalog_open"),
        BotActivityAction.ProductOpen => new(action, "navigation", "product_open", true),
        BotActivityAction.QuantityEnter => new(action, "quantity", "quantity_enter", true, true),
        BotActivityAction.CheckoutOpen => new(action, "checkout", "checkout_open", true),
        BotActivityAction.SupportOpen => new(action, "support", "support_open"),
        BotActivityAction.SupportReply => new(action, "support", "support_reply", TicketRequired: true),
        BotActivityAction.Subscribe => new(action, "subscription", "subscribe", true),
        BotActivityAction.Unsubscribe => new(action, "subscription", "unsubscribe", true),
        BotActivityAction.DiscountsOpen => new(action, "discount", "discounts_open"),
        BotActivityAction.PolicyCheck => new(action, "policy", "policy_check"),
        BotActivityAction.CheckoutCreated => new(action, "checkout", "checkout_created", true, true, OrderRequired: true),
        BotActivityAction.FlowExpired => new(action, "flow", "flow_expired", RevisionRequired: true),
        _ => throw new ArgumentException("Unknown activity action.")
    };
    public static BotActivityDefinition FromCode(string code) => Enum.GetValues<BotActivityAction>().Select(Get).FirstOrDefault(d => d.Code == code) is var value && value.Code is not null ? value : throw new ArgumentException("Unknown activity action.");
    public static string Source(BotActivitySource source) => source switch
    {
        BotActivitySource.Command => "command",
        BotActivitySource.Callback => "callback",
        BotActivitySource.Flow => "flow",
        BotActivitySource.System => "system",
        _ => throw new ArgumentException("Unknown activity source.")
    };
    public static string Outcome(BotActivityOutcome outcome) => outcome switch
    {
        BotActivityOutcome.Accepted => "accepted",
        BotActivityOutcome.Rejected => "rejected",
        BotActivityOutcome.Unavailable => "unavailable",
        BotActivityOutcome.Blocked => "blocked",
        BotActivityOutcome.Cancelled => "cancelled",
        BotActivityOutcome.Duplicate => "duplicate",
        _ => throw new ArgumentException("Unknown activity outcome.")
    };
    public static void Validate(BotActivityAction action, BotActivitySource source, BotActivityOutcome outcome, BotActivityContext context)
    {
        var definition = Get(action); _ = Source(source); _ = Outcome(outcome);
        if (context.Product == Guid.Empty || context.Order == Guid.Empty || context.Ticket == Guid.Empty || context.Quantity < 1 || context.FlowRevision < 0 || context.TelegramUpdate < 0 ||
            (definition.ProductRequired && context.Product is null) || (definition.QuantityRequired && context.Quantity is null) || (definition.TicketRequired && context.Ticket is null) || (definition.OrderRequired && context.Order is null) || (definition.RevisionRequired && context.FlowRevision is null)) throw new ArgumentException("Invalid typed activity context.");
    }
    public static string TelegramIdentity(long update)
    { if (update < 0) throw new ArgumentOutOfRangeException(nameof(update)); return "telegram:" + update.ToString(CultureInfo.InvariantCulture); }
}
