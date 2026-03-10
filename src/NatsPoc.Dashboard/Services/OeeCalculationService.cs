using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using NatsPoc.Dashboard.Data;
using NatsPoc.Dashboard.Hubs;
using NatsPoc.Dashboard.Models;

namespace NatsPoc.Dashboard.Services;

/// <summary>
/// Background service that calculates OEE (Overall Equipment Effectiveness)
/// for each device every 10 seconds and pushes updates via SignalR.
///
/// OEE = Availability × Performance × Quality
/// - Availability = RunTime / PlannedTime = (PlannedTime - Downtime) / PlannedTime
/// - Performance = TotalParts / (IdealRate × RunTimeSeconds)
/// - Quality = GoodParts / TotalParts
/// </summary>
public sealed class OeeCalculationService : BackgroundService
{
    private readonly ILogger<OeeCalculationService> _logger;
    private readonly IHubContext<DashboardHub> _hubContext;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DateTimeOffset _serviceStartedAt = DateTimeOffset.UtcNow;

    /// <summary>
    /// Ideal parts per second per device — matches the simulator's IdealPartsPerCycle
    /// with a 1-second heartbeat interval.
    /// </summary>
    public static readonly Dictionary<string, int> IdealPartsPerSecond = new()
    {
        ["PLC-PRESS-001"] = 10,
        ["PLC-CONV-002"] = 25,
        ["PLC-WELD-003"] = 5,
        ["PLC-PACK-004"] = 20,
        ["PLC-OVEN-005"] = 8,
    };

    public static readonly string[] AllDeviceIds =
    [
        "PLC-PRESS-001", "PLC-CONV-002", "PLC-WELD-003", "PLC-PACK-004", "PLC-OVEN-005"
    ];

    public OeeCalculationService(
        ILogger<OeeCalculationService> logger,
        IHubContext<DashboardHub> hubContext,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _hubContext = hubContext;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OEE calculation service started — recalculating every 10s.");

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var snapshots = await CalculateAllOeeAsync();
                var plantOee = CalculatePlantOee(snapshots);

                var payload = snapshots.ToDictionary(
                    s => s.DeviceId,
                    s => new
                    {
                        availability = Math.Round(s.Availability, 4),
                        performance = Math.Round(s.Performance, 4),
                        quality = Math.Round(s.Quality, 4),
                        oee = Math.Round(s.Oee, 4),
                        totalParts = s.TotalPartsProduced,
                        goodParts = s.GoodParts,
                        rejects = s.TotalRejects,
                        plantOee = Math.Round(plantOee, 4),
                    });

                await _hubContext.Clients.All.SendAsync(
                    "ReceiveOeeUpdate", payload, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in OEE calculation cycle");
            }
        }
    }

    public async Task<List<OeeSnapshot>> CalculateAllOeeAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DowntimeDbContext>();

        var now = DateTimeOffset.UtcNow;
        var plannedTimeSeconds = (now - _serviceStartedAt).TotalSeconds;
        if (plannedTimeSeconds <= 0) plannedTimeSeconds = 1;

        var snapshots = new List<OeeSnapshot>();

        foreach (var deviceId in AllDeviceIds)
        {
            var snapshot = await CalculateDeviceOeeAsync(db, deviceId, plannedTimeSeconds, now);
            snapshots.Add(snapshot);
        }

        return snapshots;
    }

    private async Task<OeeSnapshot> CalculateDeviceOeeAsync(
        DowntimeDbContext db, string deviceId, double plannedTimeSeconds, DateTimeOffset now)
    {
        // Calculate downtime from DowntimeRecords within the tracking window
        // NOTE: DateTimeOffset comparisons can't be translated by SQLite provider,
        // so filter by DeviceId server-side and by date client-side.
        var downtimeRecords = (await db.DowntimeRecords
            .Where(r => r.DeviceId == deviceId)
            .ToListAsync())
            .Where(r => r.StartedAt >= _serviceStartedAt)
            .ToList();

        var totalDowntimeSeconds = 0.0;
        foreach (var record in downtimeRecords)
        {
            if (record.IsResolved && record.DurationSeconds.HasValue)
            {
                totalDowntimeSeconds += record.DurationSeconds.Value;
            }
            else if (!record.IsResolved)
            {
                // Still ongoing — count from start to now
                totalDowntimeSeconds += (now - record.StartedAt).TotalSeconds;
            }
        }

        var runTimeSeconds = Math.Max(0, plannedTimeSeconds - totalDowntimeSeconds);

        // Availability = RunTime / PlannedTime
        var availability = Math.Clamp(runTimeSeconds / plannedTimeSeconds, 0.0, 1.0);

        // Production data from ProductionRecords
        // NOTE: DateTimeOffset comparisons can't be translated by SQLite provider,
        // so filter by DeviceId server-side and by date client-side.
        var productionData = (await db.ProductionRecords
            .Where(r => r.DeviceId == deviceId)
            .ToListAsync())
            .Where(r => r.Timestamp >= _serviceStartedAt)
            .GroupBy(r => r.DeviceId)
            .Select(g => new
            {
                TotalParts = g.Sum(r => r.PartsProduced),
                TotalRejects = g.Sum(r => r.RejectCount),
            })
            .FirstOrDefault();

        var totalParts = productionData?.TotalParts ?? 0;
        var totalRejects = productionData?.TotalRejects ?? 0;

        // Performance = TotalParts / (IdealRate × RunTimeSeconds)
        var idealRate = IdealPartsPerSecond.GetValueOrDefault(deviceId, 10);
        var theoreticalMax = idealRate * runTimeSeconds;
        var performance = theoreticalMax > 0
            ? Math.Clamp(totalParts / theoreticalMax, 0.0, 1.0)
            : 0.0;

        // Quality = GoodParts / TotalParts (handle div-by-zero = 1.0)
        var goodParts = totalParts - totalRejects;
        var quality = totalParts > 0
            ? Math.Clamp((double)goodParts / totalParts, 0.0, 1.0)
            : 1.0;

        return new OeeSnapshot
        {
            DeviceId = deviceId,
            Availability = availability,
            Performance = performance,
            Quality = quality,
            TotalPartsProduced = totalParts,
            TotalRejects = totalRejects,
            PlannedTimeSeconds = plannedTimeSeconds,
            RunTimeSeconds = runTimeSeconds,
            DowntimeSeconds = totalDowntimeSeconds,
            CalculatedAt = now,
        };
    }

    public static double CalculatePlantOee(List<OeeSnapshot> snapshots)
    {
        if (snapshots.Count == 0) return 0.0;

        // Weighted average by total parts produced; fall back to simple average if no production yet
        var totalParts = snapshots.Sum(s => s.TotalPartsProduced);
        if (totalParts > 0)
        {
            return snapshots.Sum(s => s.Oee * s.TotalPartsProduced) / totalParts;
        }

        return snapshots.Average(s => s.Oee);
    }
}
