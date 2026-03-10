using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.Serializers.Json;
using NatsPoc.Shared;
using NatsPoc.Shared.Models;

namespace NatsPoc.DowntimeDetector;

/// <summary>
/// Background service that subscribes to PLC heartbeats and detects device downtime.
/// A device is considered DOWN if no heartbeat is received within the configured timeout.
///
/// NATS concept: Wildcard subscriptions — by subscribing to "plc.*.heartbeat",
/// this single subscription receives heartbeats from ALL devices. No need to know
/// device IDs in advance; new devices are auto-discovered when they first publish.
///
/// Uses DeviceTracker from Shared to manage device state tracking.
/// </summary>
public sealed class DowntimeDetectorWorker : BackgroundService
{
    private readonly ILogger<DowntimeDetectorWorker> _logger;
    private readonly IConfiguration _configuration;

    public DowntimeDetectorWorker(ILogger<DowntimeDetectorWorker> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var natsUrl = _configuration.GetValue<string>("Nats:Url") ?? "nats://localhost:4222";
        var timeoutSeconds = _configuration.GetValue<int>("Detector:TimeoutSeconds", 15);
        var checkIntervalMs = _configuration.GetValue<int>("Detector:CheckIntervalMs", 5000);

        _logger.LogInformation(
            "Connecting to NATS at {Url}. Downtime threshold: {Timeout}s, check interval: {Interval}ms",
            natsUrl, timeoutSeconds, checkIntervalMs);

        // DeviceTracker from Shared handles the up/down state logic
        var tracker = new DeviceTracker(TimeSpan.FromSeconds(timeoutSeconds));

        // Connect to NATS
        await using var nats = new NatsConnection(new NatsOpts { Url = natsUrl });
        await nats.ConnectAsync();

        _logger.LogInformation("✅ Connected to NATS! Subscribing to: {Subject}", NatsSubjects.AllHeartbeats);
        _logger.LogInformation("Waiting for heartbeats...\n");

        // JSON serializer for deserializing incoming heartbeat messages
        var serializer = NatsJsonSerializer<PlcHeartbeat>.Default;

        // Track which devices were previously down (for state transition logging)
        var previouslyDown = new HashSet<string>();

        // Task 1: Subscribe to heartbeats using NATS wildcard
        // "plc.*.heartbeat" matches: plc.PLC-PRESS-001.heartbeat, plc.PLC-WELD-003.heartbeat, etc.
        var subscriptionTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var msg in nats.SubscribeAsync<PlcHeartbeat>(
                    NatsSubjects.AllHeartbeats,
                    serializer: serializer,
                    cancellationToken: stoppingToken))
                {
                    try
                    {
                        if (msg.Data is null) continue;

                        var heartbeat = msg.Data;

                        // Record the heartbeat — DeviceTracker handles first-seen vs returning
                        tracker.RecordHeartbeat(heartbeat.DeviceId, heartbeat.Timestamp);

                        // If this device was previously marked as down, announce recovery
                        if (previouslyDown.Remove(heartbeat.DeviceId))
                        {
                            _logger.LogInformation("✅ [{DeviceId}] is back ONLINE!", heartbeat.DeviceId);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Failed to process heartbeat from subject {Subject}", msg.Subject);
                    }
                }
            }
            catch (OperationCanceledException) { /* Expected on shutdown */ }
        }, stoppingToken);

        // Task 2: Periodic status check — detect silent devices and print status table
        var monitorTask = Task.Run(async () =>
        {
            try
            {
                // Wait a few seconds for initial device discovery
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

                while (!stoppingToken.IsCancellationRequested)
                {
                    var now = DateTimeOffset.UtcNow;
                    var statuses = tracker.GetAllStatuses(now);

                    // Check for state transitions: UP → DOWN
                    foreach (var (deviceId, status) in statuses)
                    {
                        if (!status.IsUp && !previouslyDown.Contains(deviceId))
                        {
                            // Device just went down
                            previouslyDown.Add(deviceId);
                            _logger.LogWarning("🔴 [{DeviceId}] is DOWN! No heartbeat for >{TimeoutSeconds}s", deviceId, timeoutSeconds);
                        }
                    }

                    // Print the status table
                    PrintStatusTable(statuses, now);

                    await Task.Delay(TimeSpan.FromMilliseconds(checkIntervalMs), stoppingToken);
                }
            }
            catch (OperationCanceledException) { /* Expected on shutdown */ }
        }, stoppingToken);

        await Task.WhenAll(subscriptionTask, monitorTask);

        _logger.LogInformation("Downtime detector stopped.");
    }

    /// <summary>
    /// Builds a device status table and logs it.
    /// </summary>
    private void PrintStatusTable(
        IReadOnlyDictionary<string, Shared.DeviceStatus> statuses,
        DateTimeOffset now)
    {
        if (statuses.Count == 0) return;

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("  ┌─────────────────┬────────┬──────────────────┬─────────────────┐");
        sb.AppendLine("  │ Device ID       │ Status │ Last Seen        │ Since Last      │");
        sb.AppendLine("  ├─────────────────┼────────┼──────────────────┼─────────────────┤");

        foreach (var (deviceId, status) in statuses.OrderBy(x => x.Key))
        {
            var sinceLast = now - status.LastSeen;
            var statusLabel = status.IsUp ? "  UP  " : " DOWN ";

            sb.AppendLine($"  │ {deviceId,-15} │ {statusLabel} │ {status.LastSeen:HH:mm:ss.fff}       │ {sinceLast.TotalSeconds,7:F0}s ago      │");
        }

        sb.AppendLine("  └─────────────────┴────────┴──────────────────┴─────────────────┘");

        _logger.LogInformation("Device status table:{StatusTable}", sb.ToString());
    }
}
