namespace Platform.Persistence.Entities;

public sealed class Brand
{
    public Guid Id { get; set; }
    public string Code { get; set; } = null!;
    public string InternalName { get; set; } = null!;
    public string PublicName { get; set; } = null!;
    public string Market { get; set; } = null!;
    public string DefaultLanguage { get; set; } = null!;
    public string? LogoAssetRef { get; set; }
    public string BotUsername { get; set; } = null!;
    public long BotId { get; set; }
    public byte[] BotTokenCiphertext { get; set; } = null!;
    public byte[] BotToken { get; set; } = null!;
    public string BotTokenKeyId { get; set; } = null!;
    public bool Active { get; set; }
    public bool AllowWallet { get; set; } = true;
    public bool AllowCrypto { get; set; }
    public bool AllowBank { get; set; }
    public bool AllowWalletTopup { get; set; }
    public bool ZeroBalanceTopup { get; set; }
    public bool RequireChannelJoin { get; set; }
    public bool SupportEnabled { get; set; }
    public bool FxEnabled { get; set; }
    public Guid? CustomerSupportAdminId { get; set; }
    public byte[]? BankNumberCiphertext { get; set; }
    public byte[]? BankNumber { get; set; }
    public byte[]? BankHolderCiphertext { get; set; }
    public byte[]? BankHolder { get; set; }
    public string? BankKeyId { get; set; }
    public byte[]? BankCardFingerprint { get; set; }
    public DateTime? BankChangedAt { get; set; }
    public decimal? CurrentFx { get; set; }
    public string? FxSource { get; set; }
    public DateTime? FxFetchedAt { get; set; }
    public string? FxErrorCode { get; set; }
    public decimal PricingRoundingStep { get; set; } = 5000m;
    public long MaxExchangeRateAgeSeconds { get; set; } = 3600L;
    public long TelegramUpdateHighwater { get; set; } = -1L;
    public byte[] TelegramUpdateWindow { get; set; } = null!;
    public long PolicyVersion { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
