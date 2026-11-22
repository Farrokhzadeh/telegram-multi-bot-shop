namespace Platform.Persistence.Entities;

public sealed class LegacyIdMap
{
    public string SourceBrand { get; set; } = null!;
    public string SourceTable { get; set; } = null!;
    public string SourceKeyKind { get; set; } = null!;
    public string LegacyId { get; set; } = null!;
    public string TargetKind { get; set; } = null!;
    public Guid TargetId { get; set; }
    public Guid? TargetBrandId { get; set; }
    public byte[] SourceFingerprint { get; set; } = null!;
    public Guid ImportBatch { get; set; }
    public string ImportManifestRef { get; set; } = null!;
    public DateTime MigratedAt { get; set; }
}
