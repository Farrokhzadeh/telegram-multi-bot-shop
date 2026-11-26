namespace Platform.Persistence;

internal static class DeliveryManifestEncoding
{
    internal const int MaximumBytes = 16777216;

    internal static void WriteHeader(BinaryWriter writer, int quantity)
    {
        writer.Write(1);
        writer.Write(quantity);
    }

    internal static void WritePart(BinaryWriter writer, int unit, string kind, ReadOnlySpan<byte> content)
    {
        writer.Write(unit);
        writer.Write(kind);
        writer.Write(content.Length);
        writer.Write(content);
    }

    internal static string Reference(Guid order, Guid generation) => $"manifest:{order:D}:{generation:D}";
}
