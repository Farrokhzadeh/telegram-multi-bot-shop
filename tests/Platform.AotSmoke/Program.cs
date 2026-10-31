using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpgsqlTypes;
using Platform.Core;
using Platform.Migration;
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
    await using var command = database.Data.CreateCommand("SELECT @amount::numeric");
    const decimal amount = 123456789012.123456789012m;
    command.Parameters.AddWithValue("amount", NpgsqlDbType.Numeric, amount);
    if ((decimal)(await command.ExecuteScalarAsync())! != amount)
    {
        throw new InvalidOperationException("Native Npgsql decimal mapping failed.");
    }
}

Console.WriteLine("PASS: native JSON, AES-GCM scope, legacy Fernet and disposable PostgreSQL/Npgsql.");
