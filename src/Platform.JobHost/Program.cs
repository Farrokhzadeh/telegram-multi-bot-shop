using Platform.Core;
using Platform.JobHost;

if (args is ["--serve-sqlite-staging"]) return await SqliteStagingWorker.RunAsync();
return HostBootstrap.Run(args, "JobHost");
