using Microsoft.EntityFrameworkCore;
using NatsPoc.Dashboard.Data;
using NatsPoc.Dashboard.Hubs;
using NatsPoc.Dashboard.Models;
using NatsPoc.Dashboard.Services;
using NatsPoc.Shared;

var builder = WebApplication.CreateBuilder(args);

// DeviceTracker as singleton — shared between the SignalR hub and background services
var timeoutSeconds = builder.Configuration.GetValue<int>("Detector:TimeoutSeconds", 5);
builder.Services.AddSingleton(new DeviceTracker(TimeSpan.FromSeconds(timeoutSeconds)));

// SQLite downtime history
var dbPath = Path.Combine(builder.Environment.ContentRootPath, "downtime.db");
builder.Services.AddDbContext<DowntimeDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

builder.Services.AddSignalR();
builder.Services.AddHostedService<NatsHeartbeatService>();
builder.Services.AddHostedService<DeviceStatusMonitorService>();
builder.Services.AddHostedService<OeeCalculationService>();

// CORS — allow any origin for development
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyHeader()
              .AllowAnyMethod()
              .SetIsOriginAllowed(_ => true)
              .AllowCredentials());
});

var app = builder.Build();

// Ensure SQLite database is created at startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DowntimeDbContext>();
    db.Database.EnsureCreated();
}

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapHub<DashboardHub>("/hubs/dashboard");

// Downtime history API
app.MapGet("/api/downtimes", async (DowntimeDbContext db, string? deviceId) =>
{
    var query = db.DowntimeRecords.AsQueryable();

    if (!string.IsNullOrWhiteSpace(deviceId))
        query = query.Where(r => r.DeviceId == deviceId);

    var records = await query
        .OrderByDescending(r => r.StartedAt)
        .Take(100)
        .ToListAsync();

    return Results.Ok(records);
});

// OEE API
app.MapGet("/api/oee", async (DowntimeDbContext db) =>
{
    var deviceIds = OeeCalculationService.AllDeviceIds;
    var now = DateTimeOffset.UtcNow;
    var snapshots = new Dictionary<string, OeeSnapshot>();

    foreach (var deviceId in deviceIds)
    {
        var snapshot = await CalculateDeviceOee(db, deviceId, now);
        snapshots[deviceId] = snapshot;
    }

    var deviceSnapshots = snapshots.Values.ToList();
    var plantTotalPlanned = deviceSnapshots.Sum(s => s.PlannedTimeSeconds);
    var plantTotalRun = deviceSnapshots.Sum(s => s.RunTimeSeconds);
    var plantTotalDowntime = deviceSnapshots.Sum(s => s.DowntimeSeconds);
    var plantTotalParts = deviceSnapshots.Sum(s => s.TotalPartsProduced);
    var plantTotalRejects = deviceSnapshots.Sum(s => s.TotalRejects);

    var plantAvailability = plantTotalPlanned > 0 ? plantTotalRun / plantTotalPlanned : 0;
    var plantPerformance = plantTotalRun > 0 && plantTotalParts > 0
        ? plantTotalParts / deviceSnapshots.Sum(s => OeeCalculationService.IdealPartsPerSecond.GetValueOrDefault(s.DeviceId, 10) * s.RunTimeSeconds)
        : 0;
    plantPerformance = Math.Min(plantPerformance, 1.0);
    var plantQuality = plantTotalParts > 0 ? (double)(plantTotalParts - plantTotalRejects) / plantTotalParts : 1.0;

    snapshots["plant"] = new OeeSnapshot
    {
        DeviceId = "plant",
        Availability = Math.Round(plantAvailability, 4),
        Performance = Math.Round(plantPerformance, 4),
        Quality = Math.Round(plantQuality, 4),
        TotalPartsProduced = plantTotalParts,
        TotalRejects = plantTotalRejects,
        PlannedTimeSeconds = plantTotalPlanned,
        RunTimeSeconds = plantTotalRun,
        DowntimeSeconds = plantTotalDowntime,
        CalculatedAt = now
    };

    return Results.Ok(snapshots);
});

app.MapGet("/api/oee/{deviceId}", async (DowntimeDbContext db, string deviceId) =>
{
    if (!OeeCalculationService.AllDeviceIds.Contains(deviceId))
        return Results.NotFound(new { error = $"Unknown device: {deviceId}" });

    var snapshot = await CalculateDeviceOee(db, deviceId, DateTimeOffset.UtcNow);
    return Results.Ok(snapshot);
});

app.Run();

static async Task<OeeSnapshot> CalculateDeviceOee(DowntimeDbContext db, string deviceId, DateTimeOffset now)
{
    var productionRecords = await db.ProductionRecords
        .Where(p => p.DeviceId == deviceId)
        .ToListAsync();

    var totalParts = productionRecords.Sum(p => p.PartsProduced);
    var totalRejects = productionRecords.Sum(p => p.RejectCount);

    var downtimeRecords = await db.DowntimeRecords
        .Where(d => d.DeviceId == deviceId)
        .ToListAsync();

    var totalDowntimeSeconds = downtimeRecords
        .Sum(d => (d.EndedAt ?? now).ToUnixTimeSeconds() - d.StartedAt.ToUnixTimeSeconds());

    // Use actual data window: earliest production record to now
    var earliestRecord = productionRecords.MinBy(p => p.Timestamp);
    var plannedTimeSeconds = earliestRecord != null
        ? Math.Max(1, (now - earliestRecord.Timestamp).TotalSeconds)
        : 0.0;
    var runTimeSeconds = Math.Max(0, plannedTimeSeconds - totalDowntimeSeconds);

    var availability = plannedTimeSeconds > 0 ? runTimeSeconds / plannedTimeSeconds : 0;

    var idealRate = OeeCalculationService.IdealPartsPerSecond.GetValueOrDefault(deviceId, 10);
    var performance = (runTimeSeconds > 0 && idealRate > 0)
        ? totalParts / (idealRate * runTimeSeconds)
        : 0;
    performance = Math.Min(performance, 1.0);

    var quality = totalParts > 0
        ? (double)(totalParts - totalRejects) / totalParts
        : 1.0;

    return new OeeSnapshot
    {
        DeviceId = deviceId,
        Availability = Math.Round(availability, 4),
        Performance = Math.Round(performance, 4),
        Quality = Math.Round(quality, 4),
        TotalPartsProduced = totalParts,
        TotalRejects = totalRejects,
        PlannedTimeSeconds = plannedTimeSeconds,
        RunTimeSeconds = runTimeSeconds,
        DowntimeSeconds = totalDowntimeSeconds,
        CalculatedAt = now
    };
}
