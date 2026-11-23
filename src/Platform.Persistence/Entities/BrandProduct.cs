namespace Platform.Persistence.Entities;

public sealed class BrandProduct
{
    public Guid BrandId { get; set; }
    public Guid ProductId { get; set; }
    public bool Visible { get; set; }
    public string TitleMode { get; set; } = null!;
    public string? TitleOverride { get; set; }
    public string DescriptionMode { get; set; } = null!;
    public string? DescriptionOverride { get; set; }
    public string IconMode { get; set; } = null!;
    public string? IconOverride { get; set; }
    public string GuidanceMode { get; set; } = null!;
    public string? GuidanceOverride { get; set; }
    public decimal PriceUsdt { get; set; }
    public decimal? PriceToman { get; set; }
    public bool AutoPriceToman { get; set; } = true;
    public bool AllowWallet { get; set; } = true;
    public bool AllowCrypto { get; set; }
    public bool AllowBank { get; set; }
    public int MinQuantity { get; set; } = 1;
    public int MaxQuantity { get; set; } = 100;
    public long Revision { get; set; }
    public DateTime UpdatedAt { get; set; }
}
