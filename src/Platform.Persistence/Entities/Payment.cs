namespace Platform.Persistence.Entities;

public sealed class Payment
{
    public Guid Id { get; set; }
    public Guid? OrderId { get; set; }
    public Guid? TopupUserId { get; set; }
    public string PaymentMethod { get; set; } = null!;
    public string QuoteCurrency { get; set; } = null!;
    public decimal AmountDue { get; set; }
    public decimal? AmountReceived { get; set; }
    public string? ProviderCode { get; set; }
    public string? ProviderPaymentRef { get; set; }
    public DateTime? HostedInvoiceStartedAt { get; set; }
    public string? HostedInvoiceRef { get; set; }
    public string? HostedInvoiceUrl { get; set; }
    public string IdempotencyRef { get; set; } = null!;
    public string? NetworkAsset { get; set; }
    public decimal? ExpectedNetworkAmount { get; set; }
    public string? IpnKeyId { get; set; }
    public byte[]? IpnSignature { get; set; }
    public byte[]? IpnPayloadSha256 { get; set; }
    public DateTime? IpnVerifiedAt { get; set; }
    public decimal? NetworkAmount { get; set; }
    public long? ProviderConfirmations { get; set; }
    public DateTime? FirstPaymentObservedAt { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public string Status { get; set; } = null!;
    public string? ProviderStatusCode { get; set; }
    public byte[]? BankDestinationCiphertext { get; set; }
    public string? BankDestinationKeyId { get; set; }
    public string? ReceiptAssetRef { get; set; }
    public Guid? ReviewAdminId { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewReason { get; set; }
    public Guid? CompensatingWalletEntryId { get; set; }
    public string? EvidenceAssetRef { get; set; }
    public byte[]? PendingEvidenceCiphertext { get; set; }
    public string? PendingEvidenceKeyId { get; set; }
    public byte[]? EvidenceDigest { get; set; }
    public string? LegacyKind { get; set; }
    public DateTime CreatedAt { get; set; }
}
