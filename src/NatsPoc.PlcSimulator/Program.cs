using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NatsPoc.PlcSimulator;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<PlcSimulatorWorker>();
var host = builder.Build();
host.Run();
