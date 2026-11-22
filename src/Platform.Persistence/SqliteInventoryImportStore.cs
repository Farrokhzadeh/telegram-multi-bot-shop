using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
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
        byte[] commitment = audit.Fingerprint(actor.AdminId);
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            await SqliteAdminSessionStore.RequireActiveAsync(tx, actor, token);
            using (var previous = SqliteWallet.Command(tx,
                "SELECT command_sha256 FROM audit_events WHERE id=$id", ("$id", command)))
            {
                object? prior = await previous.ExecuteScalarAsync(token);
                if (prior is byte[] value)
                {
                    if (!CryptographicOperations.FixedTimeEquals(value, commitment))
                        throw new UnauthorizedAccessException("Inventory import command identity conflict.");
                    return 0;
                }
            }
            using (var productScope = SqliteWallet.Command(tx,
                "SELECT 1 FROM products WHERE id=$product", ("$product", product)))
                if (await productScope.ExecuteScalarAsync(token) is null)
                    throw new UnauthorizedAccessException("Inventory product is unavailable.");
            string key = secrets.CurrentInventoryKeyId;
            var keys = new List<string>();
            using (var list = SqliteWallet.Command(tx,
                "SELECT DISTINCT credential_key_id FROM inventory_items ORDER BY credential_key_id"))
            await using (var rows = await list.ExecuteReaderAsync(token))
                while (await rows.ReadAsync(token))
                {
                    if (keys.Count >= 32) throw new UnauthorizedAccessException("Inventory key history needs review.");
                    keys.Add(rows.GetString(0));
                }
            if (!keys.Contains(key, StringComparer.Ordinal)) keys.Add(key);
            using (var eventWrite = SqliteWallet.Command(tx, """
                INSERT INTO audit_events(id,actor_kind,actor_admin_id,action,entity_kind,
                    entity_id,reason,correlation_id,command_sha256)
                VALUES ($id,'admin',$admin,'stock_import','product',$product,
                    'operator_review',$id,$hash)
                """, ("$id", command), ("$admin", actor.AdminId),
                ("$product", product), ("$hash", commitment)))
                if (await eventWrite.ExecuteNonQueryAsync(token) != 1)
                    throw new InvalidOperationException("Inventory import audit was not persisted.");
            var cipher = new InventoryCipher(secrets);
            foreach (var item in items)
            {
                byte[] plain = Encoding.UTF8.GetBytes(item.Credential);
                try
                {
                    bool exists = false;
                    foreach (string known in keys)
                    {
                        byte[] fingerprint = secrets.UseInventoryKey(known,
                            bytes => HMACSHA256.HashData(bytes, plain));
                        using var previous = SqliteWallet.Command(tx, """
                            SELECT id,product_id FROM inventory_items
                            WHERE source_fingerprint=$digest LIMIT 1
                            """, ("$digest", fingerprint));
                        await using var row = await previous.ExecuteReaderAsync(token);
                        if (!await row.ReadAsync(token)) continue;
                        if (row.GetGuid(0) != item.Id || row.GetGuid(1) != product)
                            throw new InvalidOperationException("Duplicate inventory content.");
                        exists = true;
                        break;
                    }
                    if (exists) continue;
                    byte[] digest = secrets.UseInventoryKey(key,
                        bytes => HMACSHA256.HashData(bytes, plain));
                    using var insert = SqliteWallet.Command(tx, """
                        INSERT INTO inventory_items(id,product_id,credential_ciphertext,
                            credential_key_id,source_fingerprint,origin_kind)
                        VALUES ($id,$product,$cipher,$key,$digest,'local_import')
                        """, ("$id", item.Id), ("$product", product),
                        ("$cipher", cipher.Encrypt(item.Id, plain)),
                        ("$key", key), ("$digest", digest));
                    if (await insert.ExecuteNonQueryAsync(token) != 1)
                        throw new InvalidOperationException("Inventory import was not persisted.");
                }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
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
