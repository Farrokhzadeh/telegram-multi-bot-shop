using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Platform.Core;
using Platform.Persistence;
using Platform.SchemaRunner;
using Platform.Security;
namespace Platform.Testing;

public sealed record CutoverPaymentKey(Guid Run, Guid Payment, Guid Member, string Origin, string Provider, string Secret) { public override string ToString() => "CutoverPaymentKey [redacted]"; }
public sealed record CutoverPaymentInput(Guid Run, Guid Nonce, string Event, string Body, string Signature) { public override string ToString() => "CutoverPaymentInput [redacted]"; }
public sealed record CutoverPaymentVerification(Guid Run, Guid Nonce, string BodySha256, string Event, bool Verified);
public sealed record CutoverPaymentArchive(Guid Run, Guid Payment, Guid Brand, string Digest, string Signature, byte[] Content) { public override string ToString() => "CutoverPaymentArchive [redacted]"; }
public sealed record CutoverPaymentAudit(Guid Run, int Count, string ManifestSha256);
[JsonSerializable(typeof(CutoverPaymentKey))]
[JsonSerializable(typeof(CutoverPaymentInput))]
[JsonSerializable(typeof(CutoverPaymentVerification))]
[JsonSerializable(typeof(CutoverPaymentArchive))]
[JsonSerializable(typeof(CutoverPaymentAudit))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class CutoverPaymentJson : JsonSerializerContext { }
public static class CutoverPaymentCapture
{
    public static VerifiedPaymentNotice Verify(Guid run, CutoverPaymentKey key, CutoverPaymentInput input)
    {
        if (run == Guid.Empty || key.Run != run || input.Run != run || input.Nonce == Guid.Empty || key.Payment == Guid.Empty || key.Member == Guid.Empty || key.Secret is null || key.Secret.Length != 64 || !key.Secret.All(char.IsAsciiHexDigit) || input.Body is null || input.Signature is null || input.Signature.Length != 128 || Encoding.UTF8.GetByteCount(input.Body) > 2048) throw new PaymentNoticeRejectedException();
        byte[] secret = Encoding.ASCII.GetBytes(key.Secret);
        try
        {
            using var verifier = new NowPaymentsIpnVerifier("stage-ipn-v1", secret);
            var notice = verifier.Verify([input.Signature], Encoding.UTF8.GetBytes(input.Body));
            string identity = Convert.ToHexStringLower(SHA256.HashData(Convert.FromHexString(input.Signature)));
            if (input.Event != identity) { notice.Dispose(); throw new PaymentNoticeRejectedException(); }
            return notice;
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }
    public static async Task<string> CaptureAsync(DbSessionFactory sessions, BrandContext context, Guid member, Guid run, CutoverPaymentKey key, CutoverPaymentInput input, Func<PaymentEvidenceContent, CancellationToken, Task<string>> archive, CancellationToken ct = default)
    {
        if (context.BrandId != SyntheticSecretFiles.BrandA || context.BotId != 1001 || member != key.Member) throw new PaymentNoticeRejectedException();
        using var notice = Verify(run, key, input);
        if (notice.Origin != key.Origin || notice.ProviderPayment != key.Provider) return "Held";
        await using (var read = await sessions.BeginReadOnlyAsync(ct))
        {
            _ = await BrandUserStore.RequireAsync(read, context, CheckoutFixture.Actor(0), member, ct);
            bool bound = await read.QuerySingleAsync(new DbStatement("SELECT EXISTS(SELECT 1 FROM shop.payments WHERE id=$1 AND topup_user_id=$2 AND order_id IS NULL AND idempotency_ref=$3 AND provider_payment_ref=$4)", DbValue.Uuid(key.Payment), DbValue.Uuid(member), DbValue.Text(key.Origin), DbValue.Text(key.Provider)), r => r.GetBoolean(0), ct);
            if (!bound) return "Held";
        }
        return (await new PaymentEvidenceStore(sessions).CaptureAsync(context, notice, archive, ct)).Outcome.ToString();
    }
}
public sealed class CutoverPaymentFiles(string directory, BrandContext context, CutoverPaymentKey key, CutoverPaymentInput input)
{
    public Task<string> SaveAsync(PaymentEvidenceContent evidence, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux() || evidence.PaymentId != key.Payment || evidence.BrandId != context.BrandId || evidence.Digest != Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input.Body)))) throw new PaymentNoticeRejectedException();
        RequireDirectory();
        string path = Path.Combine(directory, evidence.PaymentId.ToString("N") + "-" + evidence.Digest + ".json");
        if (!File.Exists(path))
        {
            var row = new CutoverPaymentArchive(key.Run, evidence.PaymentId, evidence.BrandId, evidence.Digest, input.Signature, evidence.Content.ToArray());
            try { CutoverStageRuntime.Write(path, JsonSerializer.Serialize(row, CutoverPaymentJson.Default.CutoverPaymentArchive)); }
            catch (IOException) when (File.Exists(path)) { }
        }
        byte[] plain = Read(path);
        try { if (!CryptographicOperations.FixedTimeEquals(plain, Encoding.UTF8.GetBytes(input.Body))) throw new PaymentNoticeRejectedException(); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        return Task.FromResult("protected:cutover-payment/" + key.Run.ToString("N") + "/" + evidence.PaymentId.ToString("N") + "/" + evidence.Digest);
    }
    public byte[] Read(string path)
    {
        RequireDirectory();
        if (Path.GetDirectoryName(path) != directory) throw new PaymentNoticeRejectedException();
        var row = JsonSerializer.Deserialize(ArchivePrivateFile.Read(path, 1, 8192), CutoverPaymentJson.Default.CutoverPaymentArchive) ?? throw new PaymentNoticeRejectedException();
        if (row.Run != key.Run || row.Payment != key.Payment || row.Brand != context.BrandId || Path.GetFileName(path) != row.Payment.ToString("N") + "-" + row.Digest + ".json") throw new PaymentNoticeRejectedException();
        byte[] plain = row.Content.ToArray();
        try
        {
            if (row.Digest != Convert.ToHexStringLower(SHA256.HashData(plain))) throw new PaymentNoticeRejectedException();
            var archived = new CutoverPaymentInput(key.Run, Guid.NewGuid(), Convert.ToHexStringLower(SHA256.HashData(Convert.FromHexString(row.Signature))), Encoding.UTF8.GetString(plain), row.Signature);
            using var notice = CutoverPaymentCapture.Verify(key.Run, key, archived);
            if (notice.Origin != key.Origin || notice.ProviderPayment != key.Provider) throw new PaymentNoticeRejectedException();
            return plain;
        }
        catch { CryptographicOperations.ZeroMemory(plain); throw; }
    }
    private void RequireDirectory()
    {
        if (!OperatingSystem.IsLinux() || Path.GetFullPath(directory) != directory || !Directory.Exists(directory) || new DirectoryInfo(directory).LinkTarget is not null || File.GetUnixFileMode(directory) != (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)) throw new PaymentNoticeRejectedException();
    }
}
