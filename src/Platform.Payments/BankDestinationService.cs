using Microsoft.EntityFrameworkCore;
using Entities = Platform.Persistence.Entities;
using System.Security.Cryptography;
using System.Text;
using Platform.Persistence;
using Platform.Security;

namespace Platform.Payments;

public sealed class BankDestinationService : IDisposable
{
    private readonly DbSessionFactory sessions;
    private readonly byte[] fingerprintKey;
    private bool disposed;
    public BankDestinationService(DbSessionFactory sessions, ReadOnlySpan<byte> fingerprintKey)
    {
        if (fingerprintKey.Length != 32) throw new ArgumentException("A protected shared card-fingerprint key is required.");
        this.sessions = sessions; this.fingerprintKey = fingerprintKey.ToArray();
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
                var state = await session.RunEfStepAsync(false, async (data, token) =>
                {
                    await data.LockBrandUpdateAsync(context.BrandId, token);
                    return await data.Data.Set<Entities.Brand>().Where(value => value.Id == context.BrandId && value.BotId == context.BotId).Select(value => new { Revision = value.PolicyVersion, value.Market }).SingleAsync(token);
                }, ct);
                if (state.Revision != expectedPolicyRevision || state.Market != "iran") throw new InvalidOperationException("Receiving destination revision conflict.");
                await session.RunEfStepAsync(true, async (data, token) =>
                {
                    await data.RequireAdminAsync(actor, null, token); await data.RequireBrandCommandAsync(context.BrandId, token);
                    var row = await data.Data.Set<Entities.Brand>().SingleAsync(value => value.Id == context.BrandId, token);
                    DateTime now = await data.BrandClockAsync(context.BrandId, token);
                    row.BankNumber = plain;
                    row.BankHolder = name;
                    row.BankCardFingerprint = fingerprint; row.BankChangedAt = now;
                    row.AllowBank = true; row.PolicyVersion++; row.UpdatedAt = now; return true;
                }, ct);
                await session.CommitAsync(ct);
            }
            catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
        }
        finally { CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(name); }
    }
    public void Dispose() { CryptographicOperations.ZeroMemory(fingerprintKey); disposed = true; }
    public override string ToString() => "BankDestinationService [redacted]";
}
