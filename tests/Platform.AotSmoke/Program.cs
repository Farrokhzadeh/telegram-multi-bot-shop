using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpgsqlTypes;
using Platform.Core;
using Platform.Migration;
using Platform.Persistence;
using Platform.Testing;

var status = new QualificationStatus("AotSmoke", "synthetic-qualification", false);
string json = JsonSerializer.Serialize(status, PlatformJsonContext.Default.QualificationStatus);
if (JsonSerializer.Deserialize(json, PlatformJsonContext.Default.QualificationStatus) != status)
{
    throw new InvalidOperationException("Source-generated JSON roundtrip failed.");
}

byte[] key = RandomNumberGenerator.GetBytes(32);
byte[] nonce = RandomNumberGenerator.GetBytes(12);
byte[] plaintext = Encoding.UTF8.GetBytes("synthetic credential / داده آزمایشی");
byte[] encrypted = new byte[plaintext.Length];
byte[] tag = new byte[16];
byte[] decrypted = new byte[plaintext.Length];
byte[] aad = Encoding.UTF8.GetBytes("synthetic-brand/table/row/field");
using (var aes = new AesGcm(key, 16))
{
    aes.Encrypt(nonce, plaintext, encrypted, tag, aad);
    aes.Decrypt(nonce, encrypted, tag, decrypted, aad);
    if (!CryptographicOperations.FixedTimeEquals(plaintext, decrypted))
    {
        throw new InvalidOperationException("AES-GCM roundtrip failed.");
    }

    bool rejected = false;
    try
    {
        aes.Decrypt(nonce, encrypted, tag, decrypted, Encoding.UTF8.GetBytes("another-brand"));
    }
    catch (AuthenticationTagMismatchException)
    {
        rejected = true;
    }

    if (!rejected)
    {
        throw new InvalidOperationException("Wrong-scope ciphertext was accepted.");
    }
}

CryptographicOperations.ZeroMemory(key);
CryptographicOperations.ZeroMemory(plaintext);
CryptographicOperations.ZeroMemory(decrypted);
using (var vectors = JsonDocument.Parse(File.ReadAllText(
    Path.Combine(AppContext.BaseDirectory, "fixtures", "legacy-fernet-synthetic.json"))))
{
    foreach (var vector in vectors.RootElement.GetProperty("vectors").EnumerateArray())
    {
        LegacyFieldKind fieldKind = vector.GetProperty("kind").GetString() switch
        {
            "Secret" => LegacyFieldKind.Secret,
            "Json" => LegacyFieldKind.Json,
            "Text" => LegacyFieldKind.Text,
            _ => throw new InvalidOperationException("Unknown fixture field kind.")
        };
        string result = LegacyFernetReader.Decrypt(vector.GetProperty("ciphertext").GetString()!, fieldKind,
            vector.GetProperty("keys").EnumerateArray().Select(v => v.GetString()!));
        if (result != vector.GetProperty("plaintext").GetString())
        {
            throw new InvalidOperationException("Legacy Fernet interoperability failed.");
        }
    }
}

await using (var database = await DisposablePostgres.CreateAsync())
{
    var manifest = SchemaManifest.ForQualification(database.Data.ConnectionString);
    SchemaReceipt applied = await SchemaRunner.ApplyAsync(database.Data, manifest);
    SchemaReceipt repeated = await SchemaRunner.ApplyAsync(database.Data, manifest);
    if (!applied.Applied || repeated.Applied || applied.CatalogSha256 != repeated.CatalogSha256)
    {
        throw new InvalidOperationException("Native schema application/reapplication failed.");
    }

    string? operatorPath = Environment.GetEnvironmentVariable("SHOP_SCHEMA_RUNNER_PATH");
    if (operatorPath is not null)
    {
        string directory = Path.Combine(Path.GetTempPath(), "shop-schema-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string manifestPath = Path.Combine(directory, "manifest");
            string connectionPath = Path.Combine(directory, "connection");
            string receiptPath = Path.Combine(directory, "receipt");
            await File.WriteAllTextAsync(manifestPath, manifest.ToFileText());
            var operatorConnection = new Npgsql.NpgsqlConnectionStringBuilder(
                Environment.GetEnvironmentVariable("SHOP_TEST_POSTGRES_ADMIN")
                ?? throw new InvalidOperationException("Synthetic control identity is missing.")) { Database = manifest.Database };
            await File.WriteAllTextAsync(connectionPath, operatorConnection.ConnectionString);
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(manifestPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.SetUnixFileMode(connectionPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            var start = new System.Diagnostics.ProcessStartInfo(operatorPath) { UseShellExecute = false };
            foreach (string argument in new[] { "--apply-schema", "--manifest", manifestPath, "--connection-file", connectionPath, "--receipt", receiptPath })
            {
                start.ArgumentList.Add(argument);
            }
            using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Operator probe did not start.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0 || !File.Exists(receiptPath) ||
                !(await File.ReadAllTextAsync(receiptPath)).Contains("catalog_sha256=" + applied.CatalogSha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Native operator receipt probe failed.");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    await using var command = database.Data.CreateCommand("SELECT @amount::numeric");
    const decimal amount = 123456789012.123456789012m;
    command.Parameters.AddWithValue("amount", NpgsqlDbType.Numeric, amount);
    if ((decimal)(await command.ExecuteScalarAsync())! != amount)
    {
        throw new InvalidOperationException("Native Npgsql decimal mapping failed.");
    }
}

Console.WriteLine("PASS: native JSON, AES-GCM scope, legacy Fernet, 28-table schema/operator and disposable PostgreSQL/Npgsql.");
