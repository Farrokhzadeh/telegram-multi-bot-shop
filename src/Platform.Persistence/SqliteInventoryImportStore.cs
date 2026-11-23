using System.Security.Cryptography;
using System.Text;
using Platform.Security;

namespace Platform.Persistence;

public sealed class SqliteInventoryImportStore(SqliteSessionFactory sessions, SecretSnapshot secrets)
{
    public Task ImportAsync(AuthenticatedAdmin actor, Guid product, InventoryImportItem[] items,
        Guid command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (product == Guid.Empty || command == Guid.Empty || items.Length is < 1 or > 1000 ||
            items.Any(item => item is null || item.Id == Guid.Empty ||
                string.IsNullOrWhiteSpace(item.Credential) ||
                Encoding.UTF8.GetByteCount(item.Credential) > 65536) ||
            items.Select(item => item.Id).Distinct().Count() != items.Length)
            throw new ArgumentException("The inventory import is invalid.");
        string[] signatures = items.OrderBy(item => item.Id).SelectMany(item =>
            new[] { item.Id.ToString("D"), CredentialCommitment(item.Credential) }).ToArray();
        byte[] hash = AdminCommand.Hash("inventory-import-v1", signatures);
        var audit = new AuditCommand(command, AuditAction.StockImport, AuditEntity.Product,
            product, null, AuditReason.OperatorReview, command, hash);
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            if (!await AdminAuditStore.AppendSqliteAsync(tx, actor, audit, token)) return 0;
            await SqliteSessionFactory.StepEfAsync(tx, true, async (data, cancel) => { await CatalogAdministration.ImportAsync(data, actor, product, items, secrets, cancel); return true; }, token);
            await OutboxStore.EnqueueSqliteAsync(tx,
                JobIntent.Product(JobKind.DrainLocalPreorders, product), command, DateTime.UtcNow, token);
            await OutboxStore.EnqueueSqliteAsync(tx,
                JobIntent.Product(JobKind.EvaluateRestock, product), command, DateTime.UtcNow, token);
            return 0;
        }, ct);
    }

    private static string CredentialCommitment(string credential)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(credential);
        try { return Convert.ToHexString(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
