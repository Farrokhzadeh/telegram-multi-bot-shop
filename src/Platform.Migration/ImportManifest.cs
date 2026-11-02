using System.Security.Cryptography;
using System.Text;
using Platform.Persistence;

namespace Platform.Migration;

public sealed class MigrationRejectedException : Exception
{
    public MigrationRejectedException() : base("Migration evidence or target rejected; retain the source for review.") { }
}
public enum LegacyProjection { User, WalletSnapshot, WalletOpening, Transaction, Order, OrderAlias }
public sealed class LegacyMapProof
{
    private readonly byte[] fingerprint;
    public LegacyMapProof(LegacyProjection projection, string legacyId, Guid target, Guid brand, ReadOnlySpan<byte> fingerprint)
    {
        if (!Enum.IsDefined(projection) || string.IsNullOrWhiteSpace(legacyId) || legacyId.Length > 256 || legacyId.Any(char.IsControl) || target == Guid.Empty || brand == Guid.Empty || fingerprint.Length != 32) throw new MigrationRejectedException();
        Projection = projection; LegacyId = legacyId; Target = target; Brand = brand; this.fingerprint = fingerprint.ToArray();
    }
    public LegacyProjection Projection { get; }
    public string LegacyId { get; }
    public Guid Target { get; }
    public Guid Brand { get; }
    public ReadOnlyMemory<byte> Fingerprint => fingerprint;
    public string SourceTable => Projection switch { LegacyProjection.User => "users", LegacyProjection.WalletSnapshot => "users.balance.snapshot", LegacyProjection.WalletOpening => "users.balance.opening", LegacyProjection.Transaction => "transactions", LegacyProjection.Order or LegacyProjection.OrderAlias => "orders", _ => throw new MigrationRejectedException() };
    public string KeyKind => Projection == LegacyProjection.OrderAlias ? "public_order_reference" : "primary";
    public string TargetKind => Projection switch { LegacyProjection.User => "user", LegacyProjection.WalletSnapshot => "wallet", LegacyProjection.WalletOpening or LegacyProjection.Transaction => "wallet_entry", LegacyProjection.Order or LegacyProjection.OrderAlias => "order", _ => throw new MigrationRejectedException() };
    internal string Key => SourceTable + ":" + KeyKind + ":" + LegacyId;
    internal bool Same(LegacyMapProof other) => Projection == other.Projection && LegacyId == other.LegacyId && Target == other.Target && Brand == other.Brand && CryptographicOperations.FixedTimeEquals(fingerprint, other.fingerprint);
    public override string ToString() => "LegacyMapProof [redacted]";
}
public sealed class VerifiedImportManifest
{
    private readonly byte[] digest;
    private readonly byte[] body;
    private readonly Dictionary<string, LegacyMapProof> entries;
    private VerifiedImportManifest(string reference, byte[] body, SchemaManifest destination, string source, Guid brand, string revision, byte[] schemaDigest, string snapshot, byte[] snapshotDigest, Guid batch, LegacyMapProof[] proofs)
    {
        Reference = reference; this.body = body; digest = SHA256.HashData(body); Destination = destination; SourceBrand = source; TargetBrand = brand;
        SourceRevision = revision; SourceSchemaDigest = Convert.ToHexStringLower(schemaDigest); SnapshotReference = snapshot; SnapshotDigest = Convert.ToHexStringLower(snapshotDigest); Batch = batch;
        entries = proofs.ToDictionary(p => p.Key, StringComparer.Ordinal); Proofs = Array.AsReadOnly(proofs);
    }
    public string Reference { get; }
    public ReadOnlyMemory<byte> Digest => digest;
    public SchemaManifest Destination { get; }
    public string SourceBrand { get; }
    public Guid TargetBrand { get; }
    public string SourceRevision { get; }
    public string SourceSchemaDigest { get; }
    public string SnapshotReference { get; }
    public string SnapshotDigest { get; }
    public Guid Batch { get; }
    public IReadOnlyList<LegacyMapProof> Proofs { get; }
    public byte[] Encode() => body.ToArray();
    public void Require(LegacyMapProof proof)
    { if (!entries.TryGetValue(proof.Key, out var expected) || !expected.Same(proof)) throw new MigrationRejectedException(); }
    public static byte[] EncodePlan(SchemaManifest destination, string source, Guid brand, string revision, ReadOnlySpan<byte> schemaDigest, string snapshot, ReadOnlySpan<byte> snapshotDigest, Guid batch, IEnumerable<LegacyMapProof> proofs)
    {
        destination.Validate();
        var rows = proofs.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
        if (string.IsNullOrWhiteSpace(source) || source.Length > 64 || source.Any(char.IsControl) || brand == Guid.Empty || revision.Length != 40 || !revision.All(char.IsAsciiHexDigit) || schemaDigest.Length != 32 || snapshotDigest.Length != 32 || batch == Guid.Empty || rows.Length is < 1 or > 100000 || rows.Any(p => p.Brand != brand) || rows.Select(p => p.Key).Distinct(StringComparer.Ordinal).Count() != rows.Length || rows.GroupBy(p => (p.TargetKind, p.Target)).Any(g => g.Count() > 1 && (g.Key.TargetKind != "order" || g.Count(p => p.Projection == LegacyProjection.Order) > 1))) throw new MigrationRejectedException();
        Protected(snapshot);
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream, Encoding.UTF8, true);
        w.Write("SHOP-IMPORT-1"u8); Text(w, destination.ToFileText()); Text(w, source); w.Write(brand.ToByteArray()); Text(w, revision); w.Write(schemaDigest); Text(w, snapshot); w.Write(snapshotDigest); w.Write(batch.ToByteArray()); w.Write(rows.Length);
        foreach (var p in rows) { w.Write((byte)p.Projection); Text(w, p.LegacyId); w.Write(p.Target.ToByteArray()); w.Write(p.Brand.ToByteArray()); w.Write(p.Fingerprint.Span); }
        w.Flush(); if (stream.Length > 16777216) throw new MigrationRejectedException(); return stream.ToArray();
    }
    public static VerifiedImportManifest Open(string reference, ReadOnlySpan<byte> expectedDigest, ReadOnlySpan<byte> canonicalBody)
    {
        Protected(reference);
        if (expectedDigest.Length != 32 || canonicalBody.Length is < 128 or > 16777216 || !CryptographicOperations.FixedTimeEquals(expectedDigest, SHA256.HashData(canonicalBody))) throw new MigrationRejectedException();
        try
        {
            using var stream = new MemoryStream(canonicalBody.ToArray(), false); using var r = new BinaryReader(stream, Encoding.UTF8, true);
            if (!r.ReadBytes(13).AsSpan().SequenceEqual("SHOP-IMPORT-1"u8)) throw new MigrationRejectedException();
            var destination = SchemaManifest.Parse(Text(r)); string source = Text(r); Guid brand = GuidOf(r); string revision = Text(r); byte[] schema = Exact(r, 32); string snapshot = Text(r); byte[] snapshotDigest = Exact(r, 32); Guid batch = GuidOf(r);
            int count = r.ReadInt32(); if (count is < 1 or > 100000) throw new MigrationRejectedException();
            var proofs = new LegacyMapProof[count];
            for (int i = 0; i < count; i++) proofs[i] = new((LegacyProjection)r.ReadByte(), Text(r), GuidOf(r), GuidOf(r), Exact(r, 32));
            if (stream.Position != stream.Length) throw new MigrationRejectedException();
            byte[] rebuilt = EncodePlan(destination, source, brand, revision, schema, snapshot, snapshotDigest, batch, proofs);
            if (!rebuilt.AsSpan().SequenceEqual(canonicalBody)) throw new MigrationRejectedException();
            return new(reference, rebuilt, destination, source, brand, revision, schema, snapshot, snapshotDigest, batch, proofs);
        }
        catch (Exception e) when (e is EndOfStreamException or ArgumentException or InvalidOperationException or DecoderFallbackException or OverflowException) { throw new MigrationRejectedException(); }
    }
    private static void Protected(string value) { if (!value.StartsWith("protected:", StringComparison.Ordinal) || value.Length is < 11 or > 512 || value.Any(char.IsControl)) throw new MigrationRejectedException(); }
    private static void Text(BinaryWriter w, string value) { byte[] bytes = Encoding.UTF8.GetBytes(value); if (bytes.Length > 2048) throw new MigrationRejectedException(); w.Write(bytes.Length); w.Write(bytes); }
    private static string Text(BinaryReader r) { int count = r.ReadInt32(); if (count is < 0 or > 2048) throw new MigrationRejectedException(); return new UTF8Encoding(false, true).GetString(Exact(r, count)); }
    private static byte[] Exact(BinaryReader r, int count) { byte[] bytes = r.ReadBytes(count); return bytes.Length == count ? bytes : throw new MigrationRejectedException(); }
    private static Guid GuidOf(BinaryReader r) => new(Exact(r, 16));
    public override string ToString() => "VerifiedImportManifest [redacted]";
}
