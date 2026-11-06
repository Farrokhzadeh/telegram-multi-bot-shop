using Npgsql;
using Platform.Persistence;
using Platform.Migration;
using Platform.SchemaRunner;

string ReadProtectedFile(string path)
{
    if (!Path.IsPathFullyQualified(path) || !File.Exists(path) || new FileInfo(path).LinkTarget is not null)
    {
        throw new InvalidOperationException("A regular protected input file is required.");
    }

    if (OperatingSystem.IsLinux())
    {
        const UnixFileMode shared = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        if ((File.GetUnixFileMode(path) & shared) != 0)
        {
            throw new InvalidOperationException("Input files must be private to their owner.");
        }
    }
    return File.ReadAllText(path);
}

if (args is ["--inspect-wallet-register", "--register-file", var registerFile, "--receipt", var registerReceipt])
{
    try
    {
        string? root = Path.GetDirectoryName(registerFile);
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("SHOP_ALLOW_PROTECTED_WALLET_RETENTION_INSPECTION") != "1" || !Path.IsPathFullyQualified(registerFile) || root is null || Path.GetFileName(root).StartsWith("teleg-history-qualification-", StringComparison.Ordinal) != true || new DirectoryInfo(root).LinkTarget is not null || (File.GetUnixFileMode(root) & (UnixFileMode)63) != 0 || new FileInfo(registerFile).Length > 67108864 || !Path.IsPathFullyQualified(registerReceipt) || Path.GetDirectoryName(registerReceipt) != root || File.Exists(registerReceipt)) throw new MigrationRejectedException();
        await WalletRetentionInspection.RunAsync(ReadProtectedFile(registerFile), registerReceipt); Console.WriteLine("Protected wallet register verified; redacted receipt written."); return 0;
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException or KeyNotFoundException or FormatException or MigrationRejectedException)
    { Console.Error.WriteLine("Wallet register inspection rejected; retain protected inputs for review."); return 78; }
}

if (args is [var inspectMode, "--connection-file", var copyConnection, "--plan-file", var copyPlan, "--receipt", var copyReceipt] && inspectMode is "--inspect-source-copy" or "--inspect-source-orders")
{
    try { await SourceCopyInspection.RunAsync(ReadProtectedFile(copyConnection).Trim(), ReadProtectedFile(copyPlan), copyReceipt, inspectOrders: inspectMode == "--inspect-source-orders"); Console.WriteLine("Read-only source copy verified; redacted receipt written."); return 0; }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException or KeyNotFoundException or FormatException or MigrationRejectedException or NpgsqlException or Platform.Persistence.DbOperationException)
    { Console.Error.WriteLine("Source copy inspection rejected; retain protected inputs for review."); return 78; }
}

if (args.Length == 0)
{
    Console.Error.WriteLine("Explicit schema operation and protected manifest/connection files are required.");
    return 78;
}

if (args is not ["--apply-schema", "--manifest", var manifestPath, "--connection-file", var connectionPath, "--receipt", var receiptPath])
{
    Console.Error.WriteLine("Usage: --apply-schema --manifest ABSOLUTE_FILE --connection-file ABSOLUTE_FILE --receipt NEW_ABSOLUTE_FILE");
    return 64;
}

try
{
    if (!OperatingSystem.IsLinux() || !Path.IsPathFullyQualified(receiptPath) || File.Exists(receiptPath))
    {
        throw new InvalidOperationException("A new absolute receipt file on Linux is required.");
    }

    var manifest = SchemaManifest.Parse(ReadProtectedFile(manifestPath));
    string connection = ReadProtectedFile(connectionPath).Trim();
    await using var data = new NpgsqlSlimDataSourceBuilder(connection).Build();
    SchemaReceipt result = await SchemaRunner.ApplyAsync(data, manifest);
    string receipt = "revision=" + result.Revision + "\nsql_sha256=" + result.SqlSha256 +
        "\ncatalog_sha256=" + result.CatalogSha256 + "\ndatabase=" + manifest.Database + "\n";
    var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
    if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    await using var stream = new FileStream(receiptPath, options);
    await using var writer = new StreamWriter(stream);
    await writer.WriteAsync(receipt);
    await writer.FlushAsync();
    Console.WriteLine(result.Applied ? "Schema 0001 applied; protected receipt written." : "Schema 0001 verified unchanged; protected receipt written.");
    return 0;
}
catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or NpgsqlException or ArgumentException)
{
    // Never echo exception text, connection strings, SQL parameters or file contents.
    Console.Error.WriteLine(error is PostgresException postgres ? "Schema operation rejected; SQLSTATE=" + postgres.SqlState : "Schema operation rejected; no sensitive details logged.");
    return 78;
}
