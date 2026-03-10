using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using NatsPoc.Dashboard.Data;
using NatsPoc.Dashboard.Models;
using NatsPoc.Dashboard.Services;
using NatsPoc.Shared;

namespace NatsPoc.Dashboard.Hubs;

/// <summary>
/// SignalR hub for real-time dashboard updates.
/// On client connect, sends all current device statuses, recent downtime history, and OEE data.
/// The NatsHeartbeatService pushes individual updates via IHubContext.
/// </summary>
public sealed class DashboardHub : Hub
{
    private readonly DeviceTracker _tracker;
    private readonly DowntimeDbContext _db;

    public DashboardHub(DeviceTracker tracker, DowntimeDbContext db)
    {
        _tracker = tracker;
        _db = db;
    }

    public override async Task OnConnectedAsync()
    {
        var statuses = _tracker.GetAllStatuses();

        // Build a serializable dictionary with all relevant fields
        var payload = statuses.ToDictionary(
            kvp => kvp.Key,
            kvp => new
            {
                kvp.Value.IsUp,
                LastSeen = kvp.Value.LastSeen.ToString("o"),
                DownSince = kvp.Value.DownSince?.ToString("o")
            });

        await Clients.Caller.SendAsync("ReceiveAllStatuses", payload);

        // Send recent downtime history to the newly connected client
        var recentDowntimes = await _db.DowntimeRecords
            .OrderByDescending(r => r.Id)
            .Take(50)
            .Select(r => new
            {
                r.Id,
                r.DeviceId,
                r.DeviceName,
                StartedAt = r.StartedAt.ToString("o"),
                EndedAt = r.EndedAt != null ? r.EndedAt.Value.ToString("o") : null,
                r.DurationSeconds,
                r.Reason,
                r.IsResolved,
            })
            .ToListAsync();

        await Clients.Caller.SendAsync("ReceiveDowntimeHistory", recentDowntimes);

        // Send initial OEE snapshot
        await SendOeeSnapshotAsync();

        await base.OnConnectedAsync();
    }

    private async Task SendOeeSnapshotAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var oeePayload = new Dictionary<string, object>();
        var snapshots = new List<OeeSnapshot>();

        foreach (var deviceId in OeeCalculationService.IdealPartsPerSecond.Keys)
        {
            // Quick in-hub calculation for initial connect — uses same DB
            var productionData = await _db.ProductionRecords
                .Where(r => r.DeviceId == deviceId)
                .GroupBy(r => r.DeviceId)
                .Select(g => new
                {
                    TotalParts = g.Sum(r => r.PartsProduced),
                    TotalRejects = g.Sum(r => r.RejectCount),
                })
                .FirstOrDefaultAsync();

            var totalParts = productionData?.TotalParts ?? 0;
            var totalRejects = productionData?.TotalRejects ?? 0;

            var snapshot = new OeeSnapshot
            {
                DeviceId = deviceId,
                Availability = 1.0,
                Performance = 0.0,
                Quality = totalParts > 0 ? Math.Clamp((double)(totalParts - totalRejects) / totalParts, 0, 1) : 1.0,
                TotalPartsProduced = totalParts,
                TotalRejects = totalRejects,
                CalculatedAt = now,
            };
            snapshots.Add(snapshot);

            oeePayload[deviceId] = new
            {
                availability = Math.Round(snapshot.Availability, 4),
                performance = Math.Round(snapshot.Performance, 4),
                quality = Math.Round(snapshot.Quality, 4),
                oee = Math.Round(snapshot.Oee, 4),
                totalParts = snapshot.TotalPartsProduced,
                goodParts = snapshot.GoodParts,
                rejects = snapshot.TotalRejects,
                plantOee = Math.Round(OeeCalculationService.CalculatePlantOee(snapshots), 4),
            };
        }

        await Clients.Caller.SendAsync("ReceiveOeeUpdate", oeePayload);
    }
}
