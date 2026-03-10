using NatsPoc.DowntimeDetector;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<DowntimeDetectorWorker>();
var host = builder.Build();
host.Run();
