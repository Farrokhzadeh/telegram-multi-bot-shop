using Platform.Persistence;

namespace Platform.Telegram;

public enum CustomerText { OutOfStock, RestockOptIn, RestockUnsubscribe, RestockAvailable, JoinChannels, Support }
public static class Localization
{
    public static string Text(BrandPolicySnapshot policy, CustomerText key)
    {
        ArgumentNullException.ThrowIfNull(policy);
        bool persian = policy.Language.Equals("fa", StringComparison.OrdinalIgnoreCase) || policy.Language.StartsWith("fa-", StringComparison.OrdinalIgnoreCase);
        return (persian, key) switch
        {
            (true, CustomerText.OutOfStock) => "ناموجود",
            (true, CustomerText.RestockOptIn) => "یک‌بار از موجود شدن خبرم کن",
            (true, CustomerText.RestockUnsubscribe) => "لغو اطلاع‌رسانی موجودی",
            (true, CustomerText.RestockAvailable) => "این محصول اکنون موجود است. این پیام فقط اطلاع‌رسانی است و موجودی را رزرو نمی‌کند.",
            (true, CustomerText.JoinChannels) => "برای ادامه، عضو کانال‌های الزامی شوید.",
            (true, CustomerText.Support) => "پشتیبانی",
            (false, CustomerText.OutOfStock) => "Out of stock",
            (false, CustomerText.RestockOptIn) => "Notify me once when available",
            (false, CustomerText.RestockUnsubscribe) => "Cancel restock notification",
            (false, CustomerText.RestockAvailable) => "This product is available now. This notice is informational and does not reserve stock.",
            (false, CustomerText.JoinChannels) => "Join the required channels to continue.",
            (false, CustomerText.Support) => "Support",
            _ => throw new ArgumentOutOfRangeException(nameof(key))
        };
    }
    public static string? Asset(BrandPolicySnapshot policy) => policy.LogoAssetRef;
    public static string? PublicContact(BrandPolicySnapshot policy) => policy.PublicContact is string handle ? "@" + handle : null;
}
