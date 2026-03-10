using Microsoft.AspNetCore.SignalR;
using NATS.Client.Core;
using NATS.Client.Serializers.Json;
using NatsPoc.Dashboard.Data;
using NatsPoc.Dashboard.Hubs;
using NatsPoc.Dashboard.Models;
using NatsPoc.Shared;
using NatsPoc.Shared.Models;

namespace NatsPoc.Dashboard.Services;

/// <summary>
/// Background service that subscribes to NATS heartbeats and pushes
/// real-time updates to all connected SignalR clients.
/// </summary>
public sealed class NatsHeartbeatService : BackgroundService
{
    private readonly ILogger<NatsHeartbeatService> _logger;
    private readonly IConfiguration _configuration;
    private readonly DeviceTracker _tracker;
    private readonly IHubContext<DashboardHub> _hubContext;
    private readonly IServiceScopeFactory _scopeFactory;

    public NatsHeartbeatService(
        ILogger<NatsHeartbeatService> logger,
        IConfiguration configuration,
        DeviceTracker tracker,
        IHubContext<DashboardHub> hubContext,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _configuration = configuration;
        _tracker = tracker;
        _hubContext = hubContext;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var natsUrl = _configuration.GetValue<string>("Nats:Url") ?? "nats://localhost:4222";

        _logger.LogInformation("Dashboard NATS worker connecting to {Url}", natsUrl);

        await using var nats = new NatsConnection(new NatsOpts { Url = natsUrl });
        await nats.ConnectAsync();

        _logger.LogInformation("Connected to NATS. Subscribing to {Subject}", NatsSubjects.AllHeartbeats);

        var serializer = NatsJsonSerializer<PlcHeartbeat>.Default;

        await foreach (var msg in nats.SubscribeAsync<PlcHeartbeat>(
            NatsSubjects.AllHeartbeats,
            serializer: serializer,
            cancellationToken: stoppingToken))
        {
            if (msg.Data is null) continue;

            var hb = msg.Data;

            // Update shared tracker state
            _tracker.RecordHeartbeat(hb.DeviceId, hb.Timestamp);
            var status = _tracker.GetStatus(hb.DeviceId);

            // Store production data when parts were produced
            if (hb.PartsProduced > 0)
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<DowntimeDbContext>();
                db.ProductionRecords.Add(new ProductionRecord
                {
                    DeviceId = hb.DeviceId,
                    Timestamp = hb.Timestamp,
                    PartsProduced = hb.PartsProduced,
                    RejectCount = hb.RejectCount,
                });
                await db.SaveChangesAsync(stoppingToken);
            }

            // Push to all connected SignalR clients
            await _hubContext.Clients.All.SendAsync(
                "ReceiveHeartbeat",
                hb.DeviceId,
                status.IsUp,
                status.LastSeen.ToString("o"),
                status.DownSince?.ToString("o"),
                hb.Temperature,
                hb.Pressure,
                hb.IsRunning,
                hb.PartsProduced,
                hb.RejectCount,
                stoppingToken);
        }
    }
}
