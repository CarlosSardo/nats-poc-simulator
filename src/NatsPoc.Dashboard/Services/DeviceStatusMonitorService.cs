using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using NatsPoc.Dashboard.Data;
using NatsPoc.Dashboard.Hubs;
using NatsPoc.Dashboard.Models;
using NatsPoc.Shared;

namespace NatsPoc.Dashboard.Services;

/// <summary>
/// Periodic background service that evaluates device statuses and pushes
/// "device went down" events to SignalR clients.
///
/// This fills a critical gap: NatsHeartbeatService only pushes updates when
/// heartbeats ARRIVE. When a device goes silent (offline), nothing gets pushed.
/// This service checks every 2 seconds and pushes status changes for devices
/// that have timed out since the last check.
/// </summary>
public sealed class DeviceStatusMonitorService : BackgroundService
{
    private readonly ILogger<DeviceStatusMonitorService> _logger;
    private readonly DeviceTracker _tracker;
    private readonly IHubContext<DashboardHub> _hubContext;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly int _timeoutSeconds;

    // Track the last known IsUp state per device to detect transitions
    private readonly ConcurrentDictionary<string, bool> _previousIsUp = new();

    private static readonly Dictionary<string, string> DeviceNames = new()
    {
        ["PLC-PRESS-001"] = "Hydraulic Press",
        ["PLC-CONV-002"] = "Conveyor Belt",
        ["PLC-WELD-003"] = "Welding Robot",
        ["PLC-PACK-004"] = "Packaging Machine",
        ["PLC-OVEN-005"] = "Industrial Oven",
    };

    public DeviceStatusMonitorService(
        ILogger<DeviceStatusMonitorService> logger,
        DeviceTracker tracker,
        IHubContext<DashboardHub> hubContext,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration)
    {
        _logger = logger;
        _tracker = tracker;
        _hubContext = hubContext;
        _scopeFactory = scopeFactory;
        _timeoutSeconds = configuration.GetValue<int>("Detector:TimeoutSeconds", 5);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Device status monitor started — checking every 2s for offline transitions.");

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var statuses = _tracker.GetAllStatuses();

                foreach (var (deviceId, status) in statuses)
                {
                    var wasUp = _previousIsUp.GetOrAdd(deviceId, true);

                    if (wasUp != status.IsUp)
                    {
                        // Status changed — push to all clients
                        _previousIsUp[deviceId] = status.IsUp;

                        _logger.LogWarning(
                            "🔔 [{DeviceId}] status changed: {OldState} → {NewState}",
                            deviceId,
                            wasUp ? "ONLINE" : "OFFLINE",
                            status.IsUp ? "ONLINE" : "OFFLINE");

                        if (!status.IsUp)
                        {
                            await RecordDowntimeStart(deviceId, status);
                        }
                        else
                        {
                            await RecordDowntimeEnd(deviceId);
                        }

                        await _hubContext.Clients.All.SendAsync(
                            "ReceiveHeartbeat",
                            deviceId,
                            status.IsUp,
                            status.LastSeen.ToString("o"),
                            status.DownSince?.ToString("o"),
                            (double?)null,
                            (double?)null,
                            (bool?)null,
                            stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in device status monitor cycle");
            }
        }
    }

    private async Task RecordDowntimeStart(string deviceId, DeviceStatus status)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DowntimeDbContext>();

        var deviceName = DeviceNames.GetValueOrDefault(deviceId, deviceId);
        var reason = $"Heartbeat timeout ({_timeoutSeconds}s)";

        var record = new DowntimeRecord
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            StartedAt = status.DownSince ?? DateTimeOffset.UtcNow,
            Reason = reason,
            IsResolved = false,
        };

        db.DowntimeRecords.Add(record);
        await db.SaveChangesAsync();

        _logger.LogInformation("📝 Downtime recorded for [{DeviceId}] ({DeviceName}): {Reason}",
            deviceId, deviceName, reason);

        await _hubContext.Clients.All.SendAsync("ReceiveDowntimeStarted", new
        {
            record.Id,
            record.DeviceId,
            record.DeviceName,
            StartedAt = record.StartedAt.ToString("o"),
            record.Reason,
            record.IsResolved,
        });
    }

    private async Task RecordDowntimeEnd(string deviceId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DowntimeDbContext>();

        var openRecord = await db.DowntimeRecords
            .Where(r => r.DeviceId == deviceId && !r.IsResolved)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync();

        if (openRecord is null)
        {
            _logger.LogWarning("No open downtime record found for [{DeviceId}] to resolve", deviceId);
            return;
        }

        openRecord.EndedAt = DateTimeOffset.UtcNow;
        openRecord.DurationSeconds = (openRecord.EndedAt.Value - openRecord.StartedAt).TotalSeconds;
        openRecord.IsResolved = true;
        await db.SaveChangesAsync();

        _logger.LogInformation("✅ Downtime resolved for [{DeviceId}] after {Duration:F1}s",
            deviceId, openRecord.DurationSeconds);

        await _hubContext.Clients.All.SendAsync("ReceiveDowntimeResolved", new
        {
            openRecord.Id,
            openRecord.DeviceId,
            openRecord.DeviceName,
            StartedAt = openRecord.StartedAt.ToString("o"),
            EndedAt = openRecord.EndedAt?.ToString("o"),
            openRecord.DurationSeconds,
            openRecord.Reason,
            openRecord.IsResolved,
        });
    }
}
