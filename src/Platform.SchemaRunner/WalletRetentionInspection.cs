using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Platform.Migration;
namespace Platform.SchemaRunner;

internal static class WalletRetentionInspection
{
    internal static async Task RunAsync(string body, string receipt, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux()) throw new MigrationRejectedException();
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        try
        {
            using var document = JsonDocument.Parse(bytes); var sources = document.RootElement.GetProperty("sources").EnumerateArray().ToArray();
            if (sources.Length != 3) throw new MigrationRejectedException();
            byte[] digest = SHA256.HashData(bytes); using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output))
            {
                writer.WriteStartObject(); writer.WriteString("scope", "native_wallet_register_validation_only"); writer.WriteBoolean("release_ready", false); writer.WriteBoolean("target_import_executed", false); writer.WriteBoolean("target_reencryption_executed", false); writer.WriteStartArray("sources");
                foreach (var source in sources)
                {
                    string brand = source.GetProperty("brand").GetString() ?? throw new MigrationRejectedException(), archive = source.GetProperty("archive_sha256").GetString() ?? throw new MigrationRejectedException();
                    var wallets = LegacyWalletRetention.OpenAll("protected:server-local/wallet-history-v2", digest, bytes, brand, archive).Values.ToArray();
                    writer.WriteStartObject(); writer.WriteString("brand", brand); writer.WriteString("archive_sha256", archive); writer.WriteNumber("accounts", wallets.LongLength); writer.WriteNumber("manual_irr_accounts", wallets.LongCount(w => w.ManualIrrBalance)); writer.WriteNumber("retained_history", wallets.Sum(w => w.HistoryRows)); writer.WriteNumber("retained_irr_history", wallets.Sum(w => w.IrrHistoryRows)); writer.WriteNumber("retained_zero_history", wallets.Sum(w => w.ZeroHistoryRows)); writer.WriteNumber("nonzero_usd_history", wallets.Sum(w => w.ProjectedUsdHistoryRows)); writer.WriteEndObject();
                }
                writer.WriteEndArray(); writer.WriteEndObject();
            }
            await using var stream = new FileStream(receipt, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
            await stream.WriteAsync(output.ToArray(), ct); await stream.FlushAsync(ct);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
