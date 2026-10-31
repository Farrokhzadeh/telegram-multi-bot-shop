using System.Text.Json;
using System.Text.Json.Serialization;

namespace Platform.Core;

public sealed record QualificationStatus(string Host, string Mode, bool ApplicationReady);

[JsonSerializable(typeof(QualificationStatus))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class PlatformJsonContext : JsonSerializerContext
{
}

public static class HostBootstrap
{
    public const int ConfigurationRequiredExitCode = 78;

    public static int Run(string[] args, string host)
    {
        if (args is ["--qualify"])
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new QualificationStatus(host, "synthetic-qualification", false),
                PlatformJsonContext.Default.QualificationStatus));
            return 0;
        }

        Console.Error.WriteLine("This offline foundation has no live runtime mode. Use --qualify.");
        return ConfigurationRequiredExitCode;
    }
}
