using Platform.Core;
using Platform.BotHost;

if (args is ["--qualify-ingress"]) return IngressQualification.Run();
return HostBootstrap.Run(args, "BotHost");
