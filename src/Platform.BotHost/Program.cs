using Platform.Core;
using Platform.BotHost;

if (args is ["--qualify-staging-history-telegram", "--token-file", var tokenFile, "--recipient-file", var recipientFile, "--attempt-file", var attemptFile])
    return await StagingHistoryTelegramQualification.RunAsync(tokenFile, recipientFile, attemptFile);
if (args is ["--qualify-ingress"]) return IngressQualification.Run();
return HostBootstrap.Run(args, "BotHost");
