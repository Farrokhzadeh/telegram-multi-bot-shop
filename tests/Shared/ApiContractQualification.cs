using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Platform.Api;

namespace Platform.Testing;

public static class ApiContractQualification
{
    public static void Map(WebApplication app)
    {
        app.MapBrandAdministration(null!, _ => null, (_, _) => throw new InvalidOperationException(), (_, _, _) => throw new InvalidOperationException());
        app.MapCatalogAdministration(null!, null!, _ => null);
        app.MapOrderAdministration(null!, null!, null!, null!, _ => null);
        app.MapAdministrationOperations(null!);
        app.MapCampaignAdministration(null!);
        app.MapPaymentStatus(null!);
        app.MapManualDepositStatus(null!);
    }
    public static async Task RunAsync(bool export = false)
    {
        await using var app = WebApplication.CreateSlimBuilder().Build();
        Map(app);
        var document = OpenApiContract.Generate(app);
        if (document.Paths.Sum(p => p.Value.Count) != OpenApiContract.Routes.Length || document.Paths.SelectMany(p => p.Value.Values).Any(o => o.Security.Length != 1 || !o.Security[0].ContainsKey("adminBearer")))
            throw new InvalidOperationException("Administrator API contract permission mismatch.");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, ContractJsonContext.Default.ApiDocument);
        if (export)
        {
            Directory.CreateDirectory("artifacts/contracts");
            await File.WriteAllBytesAsync("artifacts/contracts/admin-api.json", bytes);
            Console.WriteLine(Convert.ToHexStringLower(SHA256.HashData(bytes)) + "  artifacts/contracts/admin-api.json");
        }
    }
}
