using System.Security.Cryptography;
using System.Text.Json;
using Platform.Migration;

namespace Platform.UnitTests;

public sealed class LegacyCipherTests
{
    [Fact]
    public void PythonGeneratedVectorsRoundtripEveryMappedSourceRepresentation()
    {
        using var vectors = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "legacy-fernet-synthetic.json")));
        foreach (var vector in vectors.RootElement.GetProperty("vectors").EnumerateArray())
        {
            string[] keys = vector.GetProperty("keys").EnumerateArray().Select(v => v.GetString()!).ToArray();
            var kind = Enum.Parse<LegacyFieldKind>(vector.GetProperty("kind").GetString()!);
            Assert.Equal(vector.GetProperty("plaintext").GetString(),
                LegacyFernetReader.Decrypt(vector.GetProperty("ciphertext").GetString()!, kind, keys));
        }
    }

    [Fact]
    public void WrongKeyAndTamperedTokenFailWithoutPlaintextFallback()
    {
        using var vectors = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "legacy-fernet-synthetic.json")));
        var vector = vectors.RootElement.GetProperty("vectors")[0];
        string ciphertext = vector.GetProperty("ciphertext").GetString()!;
        Assert.Throws<CryptographicException>(() =>
            LegacyFernetReader.Decrypt(ciphertext, LegacyFieldKind.Secret, ["wrong-public-test-key"]));
        int index = ciphertext.Length - 10;
        string tampered = ciphertext[..index] + (ciphertext[index] == 'A' ? "B" : "A") + ciphertext[(index + 1)..];
        Assert.Throws<CryptographicException>(() => LegacyFernetReader.Decrypt(tampered,
            LegacyFieldKind.Secret, vector.GetProperty("keys").EnumerateArray().Select(v => v.GetString()!)));
    }

    [Theory]
    [InlineData("public fixture")]
    [InlineData("encrypted historical note")]
    public void PlaintextRequiresAnExplicitSourceMappingException(string plaintext)
    {
        Assert.Throws<CryptographicException>(() =>
            LegacyFernetReader.Decrypt(plaintext, LegacyFieldKind.Text, []));
        Assert.Equal(plaintext, LegacyFernetReader.Decrypt(
            plaintext, LegacyFieldKind.Text, [], allowSourcePlaintext: true));
        Assert.Throws<CryptographicException>(() => LegacyFernetReader.Decrypt(
            "encjson:v2:invalid", LegacyFieldKind.Json, [], allowSourcePlaintext: true));
    }

    [Fact]
    public void UnknownFieldMappingIsRejectedEvenForPlaintext()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LegacyFernetReader.Decrypt(
            "public fixture", (LegacyFieldKind)99, [], allowSourcePlaintext: true));
    }
}
