using Platform.Core;
using Platform.BotHost;

if (args is ["--staging-history-init", "--root", var stageRoot, "--actor", var stageActor, "--bot", var stageBot])
{
    try { return await StagingHistoryBridge.InitializeAsync(stageRoot, stageActor, stageBot); }
    catch (Exception) { Console.Error.WriteLine("Staging history fixture initialization rejected."); return 78; }
}
if (args is ["--staging-history-render", "--root", var historyRoot, "--actor", var historyActor, "--bot", var historyBot, "--command", var historyCommand])
{
    try { return await StagingHistoryBridge.RenderAsync(historyRoot, historyActor, historyBot, historyCommand); }
    catch (Exception) { Console.Error.WriteLine("Staging history request rejected."); return 78; }
}
if (args is ["--qualify-staging-history-telegram", "--token-file", var tokenFile, "--recipient-file", var recipientFile, "--attempt-file", var attemptFile])
    return await StagingHistoryTelegramQualification.RunAsync(tokenFile, recipientFile, attemptFile);
if (args is ["--staging-history-preflight", "--root", var preflightRoot])
{
    try { return await StagingHistoryRuntime.PreflightAsync(preflightRoot); }
    catch (Exception) { Console.Error.WriteLine("Isolated staging history preflight rejected; no Telegram send attempted."); return 78; }
}
if (args is ["--staging-history-once", "--root", var runtimeRoot])
{
    try { return await StagingHistoryRuntime.RunAsync(runtimeRoot); }
    catch (Exception) { Console.Error.WriteLine("Isolated private history request rejected or outcome uncertain; no automatic retry."); return 78; }
}
if (args is ["--serve-staging-preview", "--root", var stagingRoot]) return await StagingPreviewBot.RunAsync(stagingRoot);
if (args is ["--qualify-ingress"]) return IngressQualification.Run();
return HostBootstrap.Run(args, "BotHost");
