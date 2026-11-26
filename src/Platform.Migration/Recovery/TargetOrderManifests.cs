using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Platform.Persistence;
using Platform.Persistence.Entities;

namespace Platform.Migration.Recovery;

internal static class TargetOrderManifests
{
    internal static async Task VerifyAsync(PlainTargetDbContext data, CancellationToken ct)
    {
        Guid? cursor = null;
        while (true)
        {
            var query = data.Set<Order>().Where(order => order.ManifestAssetRef != null && order.DeliveryGeneration != null);
            if (cursor is Guid after) query = query.Where(order => order.Id.CompareTo(after) > 0);
            var orders = await query.OrderBy(order => order.Id).Take(128)
                .Select(order => new { order.Id, order.Quantity, order.DeliveryGeneration, order.ManifestAssetRef, order.ManifestDigest }).ToListAsync(ct);
            foreach (var order in orders)
            {
                if (order.DeliveryGeneration is not Guid generation ||
                    order.ManifestAssetRef != DeliveryManifestEncoding.Reference(order.Id, generation)) continue;
                if (order.ManifestDigest is not { Length: 32 } || order.Quantity is < 1 or > 10000)
                    throw new MigrationRejectedException();
                await VerifyOrderAsync(data, order.Id, generation, order.Quantity, order.ManifestDigest, ct);
            }
            if (orders.Count < 128) return;
            cursor = orders[^1].Id;
        }
    }

    private static async Task VerifyOrderAsync(PlainTargetDbContext data, Guid order, Guid generation, int quantity, byte[] expected, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true);
        try
        {
            DeliveryManifestEncoding.WriteHeader(writer, quantity);
            int count = 0;
            while (true)
            {
                int after = count;
                var parts = await data.Set<Fulfillment>().Where(part => part.OrderId == order && part.UnitNo > after)
                    .OrderBy(part => part.UnitNo).Take(32)
                    .Select(part => new { part.UnitNo, part.Status, part.ManifestGeneration, part.ContentKind, part.DeliveredContent }).ToListAsync(ct);
                try
                {
                    foreach (var part in parts)
                    {
                        if (part.UnitNo != ++count || count > quantity || part.Status is not ("ready" or "released") ||
                            part.ManifestGeneration != generation || part.ContentKind is null || part.DeliveredContent is null ||
                            part.DeliveredContent.Length > DeliveryManifestEncoding.MaximumBytes - buffer.Length)
                            throw new MigrationRejectedException();
                        DeliveryManifestEncoding.WritePart(writer, part.UnitNo, part.ContentKind, part.DeliveredContent);
                        if (buffer.Length > DeliveryManifestEncoding.MaximumBytes) throw new MigrationRejectedException();
                    }
                }
                finally
                {
                    foreach (var part in parts)
                        if (part.DeliveredContent is not null) CryptographicOperations.ZeroMemory(part.DeliveredContent);
                }
                if (parts.Count < 32) break;
            }
            writer.Flush();
            if (count != quantity || !CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length))))
                throw new MigrationRejectedException();
        }
        finally { CryptographicOperations.ZeroMemory(buffer.GetBuffer()); }
    }
}
