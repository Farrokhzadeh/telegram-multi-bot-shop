using Platform.Core;

namespace Platform.UnitTests;

public sealed class PaymentDeadlineTests
{
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void DeadlineEqualityIsRetainedAndOnlyLaterNoEvidenceCanExpire(int ticks, bool expire)
    {
        var due = new DateTime(2030, 1, 1, 12, 30, 0, DateTimeKind.Utc);
        Assert.Equal(expire, PaymentDeadlinePolicy.CanExpire("awaiting_payment", due, due.AddTicks(ticks), false));
        Assert.False(PaymentDeadlinePolicy.CanExpire("awaiting_payment", due, due.AddDays(1), true));
        Assert.False(PaymentDeadlinePolicy.CanExpire("confirmation_pending", due, due.AddDays(1), false));
        Assert.False(PaymentDeadlinePolicy.CanExpire("under_review", due, due.AddDays(1), false));
        Assert.False(PaymentDeadlinePolicy.CanExpire("paid", due, due.AddDays(1), false));
    }
    [Theory]
    [InlineData("en", "Payment valid for 30 minutes", "Cancelled — replaced", "Failed/Expired")]
    [InlineData("fa", "پرداخت تا ۳۰ دقیقه معتبر است", "لغو شد — جایگزین شده", "ناموفق/منقضی")]
    public void InstructionsKeepTheOriginalReferenceAndExactUtcDeadline(string language, string valid, string cancelled, string expired)
    {
        Guid order = Guid.NewGuid(); var due = new DateTime(2030, 1, 1, 12, 30, 0, DateTimeKind.Utc);
        string live = PaymentDeadlinePolicy.Text(order, "awaiting_payment", null, due, language);
        Assert.Contains(valid, live, StringComparison.Ordinal); Assert.Contains("2030-01-01 12:30:00 UTC", live, StringComparison.Ordinal); Assert.Contains(order.ToString("D"), live, StringComparison.Ordinal);
        Assert.Contains(cancelled, PaymentDeadlinePolicy.Text(order, "cancelled", "replaced", due, language), StringComparison.Ordinal);
        Assert.Contains(expired, PaymentDeadlinePolicy.Text(order, "expired", "unpaid_expiry", due, language), StringComparison.Ordinal);
    }
}
