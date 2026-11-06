using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteSupport.Agent;

// The apply step of an update (2026-10-16): the NEW agent, from the checked release, stops the service, swaps the files,
// starts it again and rolls back if it does not come back. A process of its own, then gone — never the service.
if (args.Length > 0 && args[0] == "--apply-update") return AgentUpdate.RunApply(args);

// Runs as a Windows service (installed via sc.exe by the installer) or as a console for debugging.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = AgentUpdate.ServiceName);
builder.Services.AddHostedService<AgentService>();
builder.Logging.AddEventLog(s => s.SourceName = "RemoteSupportAgent");

var host = builder.Build();
host.Run();
return 0;
