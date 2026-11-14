using System.Security.Cryptography;
using System.Text;
using Platform.Persistence;
using Platform.Security;

namespace Platform.Payments;

public sealed class BankDestinationService : IDisposable
{
    private readonly DbSessionFactory sessions;
    private readonly SecretSnapshot secrets;
    private readonly byte[] fingerprintKey;
    private bool disposed;
    public BankDestinationService(DbSessionFactory sessions, SecretSnapshot secrets, ReadOnlySpan<byte> fingerprintKey)
    {
        if (fingerprintKey.Length != 32) throw new ArgumentException("A protected shared card-fingerprint key is required.");
        this.sessions = sessions; this.secrets = secrets; this.fingerprintKey = fingerprintKey.ToArray();
    }
    public async Task SaveAsync(BrandContext context, AuthenticatedAdmin actor, string number, string holder, long expectedPolicyRevision, Guid command, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (number.Length != 16 || !number.All(char.IsAsciiDigit) || string.IsNullOrWhiteSpace(holder) || holder.Length > 200 || holder.Any(char.IsControl) || expectedPolicyRevision < 0)
            throw new ArgumentException("Invalid receiving destination.");
        byte[] plain = Encoding.UTF8.GetBytes(number), name = Encoding.UTF8.GetBytes(holder);
        try
        {
            byte[] fingerprint = HMACSHA256.HashData(fingerprintKey, plain);
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"bank-destination-v1:{expectedPolicyRevision}:{Convert.ToHexString(fingerprint)}:{holder}"));
            await using var session = await sessions.BeginAsync(ct);
            try
            {
                if (!await AdminAuditStore.AppendAsync(session, actor, new(command, AuditAction.Update, AuditEntity.Brand, context.BrandId, context.BrandId, AuditReason.ConfigurationChange, command, hash), ct)) return;
                await session.AdmitWriterAsync(context, ct);
                var state = await session.QuerySingleAsync(new DbStatement("SELECT policy_version,market FROM shop.brands WHERE id=$1 AND bot_id=$2 FOR UPDATE", DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId)), r => (Revision: r.GetInt64(0), Market: r.GetString(1)), ct);
                if (state.Revision != expectedPolicyRevision || state.Market != "iran") throw new InvalidOperationException("Receiving destination revision conflict.");
                var cipher = new BrandCipher(secrets);
                await session.ExecuteAsync(new DbStatement("""
                    UPDATE shop.brands SET bank_number_ciphertext=$1,bank_holder_ciphertext=$2,bank_key_id=$3,bank_card_fingerprint=$4,bank_changed_at=clock_timestamp(),
                        allow_bank=true,policy_version=policy_version+1,updated_at=clock_timestamp() WHERE id=$5
                    """, DbValue.Binary(cipher.Encrypt(context, "brands", context.BrandId, "bank_number_ciphertext", plain)),
                    DbValue.Binary(cipher.Encrypt(context, "brands", context.BrandId, "bank_holder_ciphertext", name)), DbValue.Text(secrets.CurrentBrandKeyId(context)), DbValue.Binary(fingerprint), DbValue.Uuid(context.BrandId)), ct);
                await session.CommitAsync(ct);
            }
            catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
        }
        finally { CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(name); }
    }
    public void Dispose() { CryptographicOperations.ZeroMemory(fingerprintKey); disposed = true; }
    public override string ToString() => "BankDestinationService [redacted]";
}
