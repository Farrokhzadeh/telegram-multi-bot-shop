using System.Security.Cryptography;
using System.Text;
using Platform.Security;

namespace Platform.Migration.Recovery;

public sealed class CustomerHistoryArchive(SecretSnapshot secrets)
{
    internal byte[] OpenContent(BrandContext brand, Guid member, ReadOnlySpan<byte> sealedBytes)
    {
        if (member == Guid.Empty || sealedBytes.Length is < 34 or > 1048769 || !sealedBytes[..4].SequenceEqual("SC02"u8) ||
            sealedBytes[4] is < 1 or > 64 || sealedBytes.Length < 33 + sealedBytes[4])
            throw new UnauthorizedAccessException("Historical order archive rejected.");
        string key = Encoding.ASCII.GetString(sealedBytes.Slice(5, sealedBytes[4]));
        try { return new BrandCipher(secrets).Decrypt(brand, "customer_history", member, "orders", key, sealedBytes); }
        catch (CipherRejectedException) { throw new UnauthorizedAccessException("Historical order archive rejected."); }
    }
    public IReadOnlyList<CustomerHistoricalOrder> Open(BrandContext brand, Guid member, ReadOnlySpan<byte> sealedBytes)
    {
        byte[] plain = OpenContent(brand, member, sealedBytes);
        try { return CustomerHistoryContent.Decode(member, plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public CustomerHistoricalPage Page(BrandContext brand, Guid member, ReadOnlySpan<byte> sealedBytes, int offset, int limit = 20)
    {
        if (offset < 0 || limit is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(offset));
        return CustomerHistoryContent.Page(Open(brand, member, sealedBytes), offset, limit);
    }
}
