using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Platform.Core;
using Platform.Persistence;
using Platform.Security;
using Platform.Testing;
namespace Platform.IntegrationTests;

[Collection("PostgreSQL races")]
public sealed class CutoverPaymentCaptureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static async Task<CutoverPaymentKey> Setup(CheckoutFixture f)
    {
        await using (var policy = await f.Sessions.BeginAsync(Ct))
        { await policy.ExecuteAsync(new DbStatement("UPDATE shop.brands SET allow_crypto=true,allow_wallet_topup=true,policy_version=policy_version+1 WHERE id=$1", DbValue.Uuid(f.Brands[0])), Ct); await policy.CommitAsync(Ct); }
        Guid id = Guid.NewGuid(); var store = new PaymentEvidenceStore(f.Sessions, f.Secrets);
        _ = await store.CreateTopupAsync(f.Context(0), CheckoutFixture.Actor(0), id, new(10.25m, Currency.Usd), CryptoAsset.UsdtBsc, Ct);
        string provider = PaymentSettlementFixture.Provider(id);
        await using var bind = await f.Sessions.BeginAsync(Ct);
        await store.BindInvoiceAsync(bind, f.Context(0), CheckoutFixture.Actor(0), id, provider, 10.4m, Ct); await bind.CommitAsync(Ct);
        return new(Guid.NewGuid(), id, f.Users[0], "topup:" + id.ToString("D"), provider, new string('a', 64));
    }
    private static CutoverPaymentInput Input(CutoverPaymentKey key, string? origin = null, decimal quote = 10.25m, string status = "finished", bool formatted = false)
    {
        string canonical = PaymentNoticeFixture.Body(origin ?? key.Origin, status, asset: "usdtbsc", quote: quote, provider: key.Provider);
        string signature = Convert.ToHexStringLower(HMACSHA512.HashData(Encoding.ASCII.GetBytes(key.Secret), Encoding.UTF8.GetBytes(canonical)));
        string body = formatted ? "\n" + canonical + "\n" : canonical;
        return new(key.Run, Guid.NewGuid(), Convert.ToHexStringLower(SHA256.HashData(Convert.FromHexString(signature))), body, signature);
    }
    [Fact]
    public async Task RealCaptureAndCanonicalReplayRetainEncryptedOriginalsAndOneJobWithoutPosting()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var f = await CheckoutFixture.CreateAsync(users: 1, ct: Ct); var key = await Setup(f); long postings = await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", Ct); string root = Path.Combine(Path.GetTempPath(), "cutover-payment-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var first = Input(key); var files = new CutoverPaymentFiles(root, f.Secrets, f.Context(0), key, first);
            Assert.Equal("Ready", await CutoverPaymentCapture.CaptureAsync(f.Sessions, f.Secrets, f.Context(0), f.Users[0], key.Run, key, first, files.SaveAsync, Ct));
            string path = Directory.GetFiles(root, "*.json").Single(); byte[] original = await File.ReadAllBytesAsync(path, Ct); byte[] plain = files.Read(path);
            try { Assert.Equal(first.Body, Encoding.UTF8.GetString(plain)); } finally { CryptographicOperations.ZeroMemory(plain); }
            Assert.Equal("Replayed", await CutoverPaymentCapture.CaptureAsync(f.Sessions, f.Secrets, f.Context(0), f.Users[0], key.Run, key, first, files.SaveAsync, Ct));
            Assert.Equal(original, await File.ReadAllBytesAsync(path, Ct));
            var variant = Input(key, formatted: true); Assert.Equal(first.Event, variant.Event); var variantFiles = new CutoverPaymentFiles(root, f.Secrets, f.Context(0), key, variant);
            Assert.Equal("Replayed", await CutoverPaymentCapture.CaptureAsync(f.Sessions, f.Secrets, f.Context(0), f.Users[0], key.Run, key, variant, variantFiles.SaveAsync, Ct));
            Assert.Equal(2, Directory.GetFiles(root, "*.json").Length); Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='verify_payment' AND status='pending'", Ct)); Assert.Equal(postings, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", Ct)); Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders", Ct));
            await using var read = await f.Sessions.BeginReadOnlyAsync(Ct); Assert.Equal(0L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.payments WHERE status='settled'"), r => r.GetInt64(0), Ct));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task InvalidProofIsRejectedAndAuthenticUnboundOrMismatchedEvidenceDoesNotCreditOrReprice()
    {
        await using var f = await CheckoutFixture.CreateAsync(users: 3, ct: Ct); var key = await Setup(f); long postings = await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", Ct); int archived = 0; Task<string> Archive(ProtectedPaymentEvidence e, CancellationToken _) { archived++; return Task.FromResult("protected:synthetic/" + e.Digest); }
        var valid = Input(key);
        await Assert.ThrowsAsync<PaymentNoticeRejectedException>(() => CutoverPaymentCapture.CaptureAsync(f.Sessions, f.Secrets, f.Context(0), f.Users[0], key.Run, key, valid with { Signature = new string('0', 128) }, Archive, Ct));
        await Assert.ThrowsAsync<PaymentNoticeRejectedException>(() => CutoverPaymentCapture.CaptureAsync(f.Sessions, f.Secrets, f.Context(1), f.Users[1], key.Run, key, valid, Archive, Ct));
        Assert.Equal("Held", await CutoverPaymentCapture.CaptureAsync(f.Sessions, f.Secrets, f.Context(0), f.Users[0], key.Run, key, Input(key, origin: "unbound-source"), Archive, Ct)); Assert.Equal(0, archived);
        Assert.Equal("Review", await CutoverPaymentCapture.CaptureAsync(f.Sessions, f.Secrets, f.Context(0), f.Users[0], key.Run, key, Input(key, quote: 99), Archive, Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.payments WHERE amount_due=10.25 AND quote_currency='USD' AND status='review'", Ct)); Assert.Equal(postings, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", Ct));
    }
    [Fact]
    public async Task ArchiveAuthenticationFailureCannotBecomeAReplayedAcknowledgement()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var f = await CheckoutFixture.CreateAsync(users: 1, ct: Ct); var key = await Setup(f); long postings = await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", Ct); string root = Path.Combine(Path.GetTempPath(), "cutover-payment-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var input = Input(key); var files = new CutoverPaymentFiles(root, f.Secrets, f.Context(0), key, input); Assert.Equal("Ready", await CutoverPaymentCapture.CaptureAsync(f.Sessions, f.Secrets, f.Context(0), f.Users[0], key.Run, key, input, files.SaveAsync, Ct)); string path = Directory.GetFiles(root, "*.json").Single(); var row = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(path, Ct), CutoverPaymentJson.Default.CutoverPaymentArchive)!; row.Ciphertext[^1] ^= 1; await File.WriteAllTextAsync(path, JsonSerializer.Serialize(row, CutoverPaymentJson.Default.CutoverPaymentArchive), Ct);
            await Assert.ThrowsAsync<CipherRejectedException>(() => CutoverPaymentCapture.CaptureAsync(f.Sessions, f.Secrets, f.Context(0), f.Users[0], key.Run, key, input, files.SaveAsync, Ct)); Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='verify_payment'", Ct)); Assert.Equal(postings, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", Ct));
        }
        finally { Directory.Delete(root, true); }
    }
}
