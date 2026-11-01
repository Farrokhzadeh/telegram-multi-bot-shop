namespace Platform.Core;

public sealed class ProviderPaymentSnapshot
{
    internal ProviderPaymentSnapshot(InvoiceExpectation invoice, string status, decimal received)
    { Invoice = invoice; Status = status; Received = received; }
    public InvoiceExpectation Invoice { get; }
    public string Status { get; }
    public decimal Received { get; }
    internal long ObservedAt { get; } = System.Diagnostics.Stopwatch.GetTimestamp();
    public override string ToString() => "ProviderPaymentSnapshot [redacted]";
}
