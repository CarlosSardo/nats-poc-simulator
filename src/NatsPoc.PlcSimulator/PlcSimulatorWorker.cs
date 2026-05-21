using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.Serializers.Json;
using NatsPoc.Shared;
using NatsPoc.Shared.Models;

namespace NatsPoc.PlcSimulator;

/// <summary>
/// Background service that simulates multiple PLC devices publishing heartbeats to NATS.
///
/// Each device runs on its own Task, publishing at a fast 1-second interval.
/// Devices exhibit multiple failure patterns: random outages, brief flickers,
/// cascading failures, and gradual degradation — making downtimes clearly
/// visible in the real-time dashboard.
///
/// NATS concept: Publishing — each device sends to its own subject (plc.{id}.heartbeat).
/// </summary>
public sealed class PlcSimulatorWorker : BackgroundService
{
    private readonly ILogger<PlcSimulatorWorker> _logger;
    private readonly IConfiguration _configuration;

    private static readonly PlcDeviceConfig[] Devices =
    [
        new("PLC-PRESS-001", "Hydraulic Press",    MinTemp: 45, MaxTemp: 85,  MinPressure: 150, MaxPressure: 300, FailureProfile: FailureProfile.Frequent, IdealPartsPerCycle: 10, RejectRate: 0.03),
        new("PLC-CONV-002",  "Conveyor Belt",      MinTemp: 30, MaxTemp: 55,  MinPressure: 10,  MaxPressure: 30,  FailureProfile: FailureProfile.Flicker,  IdealPartsPerCycle: 25, RejectRate: 0.01),
        new("PLC-WELD-003",  "Welding Robot",      MinTemp: 60, MaxTemp: 120, MinPressure: 50,  MaxPressure: 100, FailureProfile: FailureProfile.LongOutage, IdealPartsPerCycle: 5, RejectRate: 0.05),
        new("PLC-PACK-004",  "Packaging Machine",  MinTemp: 25, MaxTemp: 45,  MinPressure: 20,  MaxPressure: 60,  FailureProfile: FailureProfile.Cascade,  IdealPartsPerCycle: 20, RejectRate: 0.02),
        new("PLC-OVEN-005",  "Industrial Oven",    MinTemp: 150, MaxTemp: 350, MinPressure: 5,  MaxPressure: 15,  FailureProfile: FailureProfile.Frequent, IdealPartsPerCycle: 8, RejectRate: 0.04),
        new("PLC-CNC-006",   "CNC Mill",           MinTemp: 35, MaxTemp: 75,  MinPressure: 80,  MaxPressure: 180, FailureProfile: FailureProfile.ThermalDrift, IdealPartsPerCycle: 6, RejectRate: 0.02),
        new("PLC-PAINT-007", "Paint Booth",        MinTemp: 22, MaxTemp: 38,  MinPressure: 5,   MaxPressure: 25,  FailureProfile: FailureProfile.BurstReject,  IdealPartsPerCycle: 15, RejectRate: 0.02),
    ];

    // Cascade tracking — when one device in the cascade group fails, nearby devices follow
    private static readonly string[] CascadeGroup = ["PLC-PACK-004", "PLC-CONV-002"];
    private static int _cascadeActive;

    public PlcSimulatorWorker(ILogger<PlcSimulatorWorker> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var natsUrl = _configuration.GetValue<string>("Nats:Url") ?? "nats://localhost:4222";

        _logger.LogInformation("Connecting to NATS at {Url}...", natsUrl);

        await using var nats = new NatsConnection(new NatsOpts { Url = natsUrl });
        await nats.ConnectAsync();

        _logger.LogInformation("✅ Connected to NATS! Simulating {Count} PLC devices with varied failure profiles.", Devices.Length);

        foreach (var device in Devices)
            _logger.LogInformation("  • {Id} ({Name}) — profile: {Profile}", device.Id, device.Name, device.FailureProfile);

        var serializer = NatsJsonSerializer<PlcHeartbeat>.Default;

        var tasks = Devices.Select(device =>
            SimulateDeviceAsync(nats, device, serializer, stoppingToken));

        await Task.WhenAll(tasks);
    }

    private async Task SimulateDeviceAsync(
        NatsConnection nats,
        PlcDeviceConfig device,
        INatsSerializer<PlcHeartbeat> serializer,
        CancellationToken ct)
    {
        var isOffline = false;
        var offlineUntil = DateTimeOffset.MinValue;
        var cycleCount = 0;

        // Per-device ThermalDrift state — temperature creep above MaxTemp and a proportional reject-rate boost.
        // Caps tuned so MaxTemp 75 + drift => up to ~95°C, baseline reject 0.02 => up to ~0.12.
        var driftTempOffset = 0.0;
        var driftRejectBoost = 0.0;

        // Per-device BurstReject state — UtcNow < burstUntil means we're inside an elevated-reject window.
        var burstUntil = DateTimeOffset.MinValue;

        // Stagger device startup so they don't all begin simultaneously
        await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(0, 2000)), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                cycleCount++;

                // --- Failure decision logic based on profile ---
                if (!isOffline)
                {
                    var (shouldFail, duration) = EvaluateFailure(device, cycleCount);
                    if (shouldFail)
                    {
                        offlineUntil = DateTimeOffset.UtcNow + duration;
                        isOffline = true;

                        _logger.LogWarning("⚠️  [{DeviceId}] Going OFFLINE for {Duration:F0}s ({Profile})",
                            device.Id, duration.TotalSeconds, device.FailureProfile);

                        // Cascade trigger — if this device is in the cascade group, flag others
                        if (device.FailureProfile == FailureProfile.Cascade)
                            Interlocked.Exchange(ref _cascadeActive, 1);
                    }
                }

                // Check recovery
                if (isOffline)
                {
                    if (DateTimeOffset.UtcNow >= offlineUntil)
                    {
                        isOffline = false;
                        _logger.LogInformation("✅  [{DeviceId}] Back ONLINE after outage", device.Id);

                        if (device.FailureProfile == FailureProfile.Cascade)
                            Interlocked.Exchange(ref _cascadeActive, 0);

                        // ThermalDrift: an outage doubles as a forced reset back to baseline.
                        if (device.FailureProfile == FailureProfile.ThermalDrift)
                        {
                            driftTempOffset = 0.0;
                            driftRejectBoost = 0.0;
                        }
                    }
                    else
                    {
                        await Task.Delay(500, ct);
                        continue;
                    }
                }

                // --- Per-cycle drift / burst state evolution (online path only) ---
                if (device.FailureProfile == FailureProfile.ThermalDrift)
                {
                    // Rare scheduled blip: ~0.5% per cycle returns drift to baseline.
                    if (Random.Shared.NextDouble() < 0.005)
                    {
                        if (driftTempOffset > 0.1)
                            _logger.LogInformation("🧊 [{DeviceId}] Thermal drift reset to baseline", device.Id);
                        driftTempOffset = 0.0;
                        driftRejectBoost = 0.0;
                    }
                    else
                    {
                        // Small step up each cycle, capped. ~0.2°C/cycle and ~0.001 reject/cycle.
                        driftTempOffset = Math.Min(driftTempOffset + 0.2, 20.0);
                        driftRejectBoost = Math.Min(driftRejectBoost + 0.001, 0.10);
                    }
                }

                if (device.FailureProfile == FailureProfile.BurstReject)
                {
                    if (DateTimeOffset.UtcNow >= burstUntil
                        && Random.Shared.NextDouble() < 0.06)
                    {
                        var burstSeconds = Random.Shared.Next(10, 21);
                        burstUntil = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(burstSeconds);
                        _logger.LogWarning("🎨 [{DeviceId}] Reject burst window opened for {Seconds}s",
                            device.Id, burstSeconds);
                    }
                }

                // Simulate production output with ±20% random variation
                var variation = 0.8 + (Random.Shared.NextDouble() * 0.4); // 0.8 to 1.2
                var partsProduced = (int)Math.Round(device.IdealPartsPerCycle * variation);

                // Effective reject rate: baseline + drift boost, or burst-window override.
                var effectiveRejectRate = device.RejectRate + driftRejectBoost;
                if (device.FailureProfile == FailureProfile.BurstReject
                    && DateTimeOffset.UtcNow < burstUntil)
                {
                    effectiveRejectRate = 0.25 + Random.Shared.NextDouble() * 0.10; // [0.25, 0.35]
                }

                var rejectCount = 0;
                foreach (var _ in Enumerable.Range(0, partsProduced))
                {
                    if (Random.Shared.NextDouble() < effectiveRejectRate)
                        rejectCount++;
                }

                // Build heartbeat with simulated sensor readings
                var heartbeat = new PlcHeartbeat
                {
                    DeviceId = device.Id,
                    Timestamp = DateTimeOffset.UtcNow,
                    Temperature = Math.Round(
                        Random.Shared.NextDouble() * (device.MaxTemp - device.MinTemp)
                        + device.MinTemp + driftTempOffset, 1),
                    Pressure = Math.Round(
                        Random.Shared.NextDouble() * (device.MaxPressure - device.MinPressure) + device.MinPressure, 1),
                    IsRunning = true,
                    PartsProduced = partsProduced,
                    RejectCount = rejectCount,
                };

                var subject = NatsSubjects.ForDevice(device.Id);
                await nats.PublishAsync(subject, heartbeat, serializer: serializer, cancellationToken: ct);

                _logger.LogInformation(
                    "📡 [{DeviceId}] → {Subject}  |  Temp={Temp}°C  Psi={Pressure}",
                    device.Id, subject, heartbeat.Temperature, heartbeat.Pressure);

                // Fast 1-second heartbeat so transitions are visible in the dashboard
                await Task.Delay(1000, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error publishing heartbeat for {DeviceId}", device.Id);
                await Task.Delay(2000, ct);
            }
        }

        _logger.LogInformation("[{DeviceId}] Simulator stopped.", device.Id);
    }

    /// <summary>
    /// Evaluates whether a device should fail this cycle based on its failure profile.
    /// Returns (shouldFail, duration).
    /// </summary>
    private static (bool ShouldFail, TimeSpan Duration) EvaluateFailure(PlcDeviceConfig device, int cycle)
    {
        var roll = Random.Shared.NextDouble();

        switch (device.FailureProfile)
        {
            case FailureProfile.Frequent:
                // ~20% chance per cycle, short outages (4-10s) — lots of on/off transitions
                if (roll < 0.20)
                    return (true, TimeSpan.FromSeconds(Random.Shared.Next(4, 11)));
                break;

            case FailureProfile.Flicker:
                // ~15% chance, very brief outages (3-6s) — rapid flicker pattern
                if (roll < 0.15)
                    return (true, TimeSpan.FromSeconds(Random.Shared.Next(3, 7)));
                // Occasionally a longer outage (2% chance, 10-20s)
                if (roll < 0.17)
                    return (true, TimeSpan.FromSeconds(Random.Shared.Next(10, 21)));
                break;

            case FailureProfile.LongOutage:
                // ~8% chance but longer outages (12-25s) — dramatic down periods
                if (roll < 0.08)
                    return (true, TimeSpan.FromSeconds(Random.Shared.Next(12, 26)));
                break;

            case FailureProfile.Cascade:
                // Own failure: ~10% chance, 6-12s
                if (roll < 0.10)
                    return (true, TimeSpan.FromSeconds(Random.Shared.Next(6, 13)));
                // Cascade follower: if cascade flag is active and this device is in the group, ~60% chance to follow
                if (_cascadeActive == 1 && CascadeGroup.Contains(device.Id) && roll < 0.60)
                    return (true, TimeSpan.FromSeconds(Random.Shared.Next(4, 10)));
                break;

            case FailureProfile.ThermalDrift:
                // Rare but real outage: ~3% per cycle, 8-15s. Drift state itself is handled in SimulateDeviceAsync.
                if (roll < 0.03)
                    return (true, TimeSpan.FromSeconds(Random.Shared.Next(8, 16)));
                break;

            case FailureProfile.BurstReject:
                // Stays online by design — no outage path. Burst window state is handled in SimulateDeviceAsync.
                break;
        }

        return (false, TimeSpan.Zero);
    }

    private sealed record PlcDeviceConfig(
        string Id,
        string Name,
        double MinTemp,
        double MaxTemp,
        double MinPressure,
        double MaxPressure,
        FailureProfile FailureProfile = FailureProfile.Frequent,
        int IdealPartsPerCycle = 10,
        double RejectRate = 0.03);
}

internal enum FailureProfile
{
    Frequent,     // Lots of short outages — on/off flipping
    Flicker,      // Very brief drops, occasional longer outage
    LongOutage,   // Rare but dramatic extended downtime
    Cascade,      // Triggers nearby devices to fail too
    ThermalDrift, // Stays mostly online; temperature & reject rate creep up between rare resets
    BurstReject,  // Always online; sporadic 10-20s windows of elevated reject rate
}
