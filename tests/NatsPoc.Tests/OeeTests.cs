using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NatsPoc.Dashboard.Data;
using NatsPoc.Dashboard.Hubs;
using NatsPoc.Dashboard.Models;
using NatsPoc.Dashboard.Services;
using NSubstitute;
using Xunit;

namespace NatsPoc.Tests;

/// <summary>
/// Tests for the OEE (Overall Equipment Effectiveness) feature:
///   - OeeSnapshot model (computed Oee and GoodParts properties)
///   - ProductionRecord model (computed GoodCount property)
///   - OeeCalculationService (Availability × Performance × Quality from DB data)
///   - ProductionRecord database persistence via DowntimeDbContext
///
/// All database tests use in-memory SQLite — no external database required.
/// Service tests construct the real OeeCalculationService with mocked SignalR
/// and a real in-memory SQLite context behind IServiceScopeFactory.
/// </summary>
public class OeeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<DowntimeDbContext> _dbOptions;

    public OeeTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<DowntimeDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private DowntimeDbContext CreateContext() => new(_dbOptions);

    // ──────────────────────────────────────────────
    //  OeeSnapshot Model Tests
    // ──────────────────────────────────────────────

    /// <summary>
    /// OEE is defined as Availability × Performance × Quality.
    /// The Oee property should be the product of these three factors.
    /// </summary>
    [Fact]
    public void OeeSnapshot_Oee_IsProductOfAvailabilityPerformanceQuality()
    {
        var snapshot = new OeeSnapshot
        {
            Availability = 0.90,
            Performance = 0.80,
            Quality = 0.95,
        };

        snapshot.Oee.Should().BeApproximately(0.90 * 0.80 * 0.95, 0.0001,
            "OEE = A × P × Q");
    }

    /// <summary>
    /// Perfect OEE: all factors at 100% → OEE = 1.0.
    /// </summary>
    [Fact]
    public void OeeSnapshot_PerfectOee_Returns100Percent()
    {
        var snapshot = new OeeSnapshot
        {
            Availability = 1.0,
            Performance = 1.0,
            Quality = 1.0,
        };

        snapshot.Oee.Should().Be(1.0, "perfect A×P×Q = 1.0");
    }

    /// <summary>
    /// Zero availability means the machine never ran — OEE must be 0.
    /// </summary>
    [Fact]
    public void OeeSnapshot_ZeroAvailability_ReturnsZeroOee()
    {
        var snapshot = new OeeSnapshot
        {
            Availability = 0.0,
            Performance = 0.95,
            Quality = 0.99,
        };

        snapshot.Oee.Should().Be(0.0, "zero availability → zero OEE");
    }

    /// <summary>
    /// GoodParts is a computed property: TotalPartsProduced - TotalRejects.
    /// </summary>
    [Fact]
    public void OeeSnapshot_GoodParts_EqualsTotal_MinusRejects()
    {
        var snapshot = new OeeSnapshot
        {
            TotalPartsProduced = 1000,
            TotalRejects = 50,
        };

        snapshot.GoodParts.Should().Be(950, "1000 total - 50 rejects = 950 good");
    }

    /// <summary>
    /// World-class OEE is generally ≥ 85%. Verify a realistic scenario:
    /// A=0.90, P=0.95, Q=0.99 ≈ 0.846 (just under 85%).
    /// </summary>
    [Fact]
    public void OeeSnapshot_WorldClassOee_Above85Percent()
    {
        var snapshot = new OeeSnapshot
        {
            Availability = 0.90,
            Performance = 0.95,
            Quality = 0.999,
        };

        snapshot.Oee.Should().BeGreaterOrEqualTo(0.85,
            "A=0.90 × P=0.95 × Q=0.999 ≈ 0.854 — world-class threshold");
    }

    // ──────────────────────────────────────────────
    //  ProductionRecord Model Tests
    // ──────────────────────────────────────────────

    /// <summary>
    /// GoodCount is a computed property: PartsProduced - RejectCount.
    /// </summary>
    [Fact]
    public void ProductionRecord_GoodCount_IsPartsMinusRejects()
    {
        var record = new ProductionRecord
        {
            PartsProduced = 500,
            RejectCount = 12,
        };

        record.GoodCount.Should().Be(488, "500 - 12 = 488 good parts");
    }

    /// <summary>
    /// A new ProductionRecord should have a default Timestamp near UtcNow.
    /// </summary>
    [Fact]
    public void ProductionRecord_NewRecord_HasDefaultTimestamp()
    {
        var before = DateTimeOffset.UtcNow;
        var record = new ProductionRecord();
        var after = DateTimeOffset.UtcNow;

        record.Timestamp.Should().BeOnOrAfter(before);
        record.Timestamp.Should().BeOnOrBefore(after);
    }

    /// <summary>
    /// With zero parts produced and zero rejects, GoodCount should be 0 (not negative).
    /// </summary>
    [Fact]
    public void ProductionRecord_WithZeroParts_GoodCountIsZero()
    {
        var record = new ProductionRecord
        {
            PartsProduced = 0,
            RejectCount = 0,
        };

        record.GoodCount.Should().Be(0);
    }

    // ──────────────────────────────────────────────
    //  Database Tests (ProductionRecords)
    // ──────────────────────────────────────────────

    /// <summary>
    /// Basic round-trip: save a ProductionRecord to SQLite, read it back,
    /// and verify all fields survive persistence.
    /// </summary>
    [Fact]
    public async Task DbContext_CanCreateAndRetrieveProductionRecord()
    {
        var now = DateTimeOffset.UtcNow;
        var record = new ProductionRecord
        {
            DeviceId = "PLC-PRESS-001",
            Timestamp = now,
            PartsProduced = 100,
            RejectCount = 3,
        };

        await using (var writeCtx = CreateContext())
        {
            writeCtx.ProductionRecords.Add(record);
            await writeCtx.SaveChangesAsync();
        }

        await using var readCtx = CreateContext();
        var loaded = await readCtx.ProductionRecords
            .FirstOrDefaultAsync(r => r.DeviceId == "PLC-PRESS-001");

        loaded.Should().NotBeNull("record was saved to the database");
        loaded!.Id.Should().BeGreaterThan(0, "SQLite should auto-generate the Id");
        loaded.DeviceId.Should().Be("PLC-PRESS-001");
        loaded.PartsProduced.Should().Be(100);
        loaded.RejectCount.Should().Be(3);
        loaded.Timestamp.Should().BeCloseTo(now, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Multiple devices have production records. Querying by DeviceId should
    /// return only records for that specific device.
    /// </summary>
    [Fact]
    public async Task DbContext_CanQueryProductionRecordsByDeviceId()
    {
        var now = DateTimeOffset.UtcNow;
        await using (var ctx = CreateContext())
        {
            ctx.ProductionRecords.AddRange(
                new ProductionRecord { DeviceId = "PLC-PRESS-001", Timestamp = now, PartsProduced = 50, RejectCount = 1 },
                new ProductionRecord { DeviceId = "PLC-CONV-002", Timestamp = now, PartsProduced = 80, RejectCount = 2 },
                new ProductionRecord { DeviceId = "PLC-PRESS-001", Timestamp = now.AddMinutes(-5), PartsProduced = 40, RejectCount = 0 }
            );
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = CreateContext();
        var pressRecords = await readCtx.ProductionRecords
            .Where(r => r.DeviceId == "PLC-PRESS-001")
            .ToListAsync();

        pressRecords.Should().HaveCount(2, "PLC-PRESS-001 has 2 production records");
        pressRecords.Should().AllSatisfy(r => r.DeviceId.Should().Be("PLC-PRESS-001"));
    }

    /// <summary>
    /// Verify that production records can be ordered by Timestamp.
    /// SQLite doesn't support server-side DateTimeOffset ordering,
    /// so we use client-side ordering (matching the SQLite pattern from DowntimeHistoryTests).
    /// </summary>
    [Fact]
    public async Task DbContext_ProductionRecords_OrderByTimestamp()
    {
        var baseTime = DateTimeOffset.UtcNow;
        await using (var ctx = CreateContext())
        {
            ctx.ProductionRecords.AddRange(
                new ProductionRecord { DeviceId = "PLC-X", Timestamp = baseTime.AddMinutes(-10), PartsProduced = 10, RejectCount = 0 },
                new ProductionRecord { DeviceId = "PLC-X", Timestamp = baseTime, PartsProduced = 30, RejectCount = 0 },
                new ProductionRecord { DeviceId = "PLC-X", Timestamp = baseTime.AddMinutes(-5), PartsProduced = 20, RejectCount = 0 }
            );
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = CreateContext();
        var ordered = (await readCtx.ProductionRecords.ToListAsync())
            .OrderByDescending(r => r.Timestamp)
            .ToList();

        ordered.Should().HaveCount(3);
        ordered[0].PartsProduced.Should().Be(30, "most recent first");
        ordered[1].PartsProduced.Should().Be(20, "middle");
        ordered[2].PartsProduced.Should().Be(10, "oldest last");
    }

    // ──────────────────────────────────────────────
    //  OEE Calculation Service Tests
    //
    //  These construct the real OeeCalculationService with:
    //  - Real in-memory SQLite via DowntimeDbContext (behind IServiceScopeFactory)
    //  - Mocked IHubContext<DashboardHub> (SignalR)
    //  - Mocked ILogger
    //
    //  OeeCalculationService.CalculateAllOeeAsync() queries the DB directly.
    //  We also test the static CalculatePlantOee() method.
    // ──────────────────────────────────────────────

    /// <summary>
    /// With no production data and no downtime, the service should return defaults:
    /// availability = 1.0 (no downtime), performance = 0.0 (no parts), quality = 1.0 (no rejects).
    /// </summary>
    [Fact]
    public async Task OeeCalculation_WithNoData_ReturnsDefaultValues()
    {
        var (hubContext, _) = CreateMockHubContext();
        var scopeFactory = CreateScopeFactory();
        var logger = Substitute.For<ILogger<OeeCalculationService>>();

        var service = new OeeCalculationService(logger, hubContext, scopeFactory);

        var snapshots = await service.CalculateAllOeeAsync();

        snapshots.Should().HaveCount(OeeCalculationService.AllDeviceIds.Length,
            "one snapshot per known device");

        foreach (var snapshot in snapshots)
        {
            snapshot.Availability.Should().Be(1.0, "no downtime → full availability");
            snapshot.Performance.Should().Be(0.0, "no production data → zero performance");
            snapshot.Quality.Should().Be(1.0, "no parts → quality defaults to 1.0 (not NaN)");
            snapshot.Oee.Should().Be(0.0, "A=1 × P=0 × Q=1 = 0");
        }
    }

    /// <summary>
    /// Seed production records and verify the service calculates correct OEE values.
    /// </summary>
    [Fact]
    public async Task OeeCalculation_WithProductionData_CalculatesCorrectly()
    {
        // Create the service FIRST so _serviceStartedAt is set BEFORE we seed data.
        // Records must have timestamps >= _serviceStartedAt to be included.
        var (hubContext, _) = CreateMockHubContext();
        var scopeFactory = CreateScopeFactory();
        var logger = Substitute.For<ILogger<OeeCalculationService>>();

        var service = new OeeCalculationService(logger, hubContext, scopeFactory);

        // Seed production data for PLC-PRESS-001 — timestamps are now AFTER _serviceStartedAt.
        await using (var ctx = CreateContext())
        {
            ctx.ProductionRecords.AddRange(
                new ProductionRecord
                {
                    DeviceId = "PLC-PRESS-001",
                    Timestamp = DateTimeOffset.UtcNow,
                    PartsProduced = 200,
                    RejectCount = 10,
                },
                new ProductionRecord
                {
                    DeviceId = "PLC-PRESS-001",
                    Timestamp = DateTimeOffset.UtcNow,
                    PartsProduced = 300,
                    RejectCount = 5,
                }
            );
            await ctx.SaveChangesAsync();
        }

        var snapshots = await service.CalculateAllOeeAsync();
        var pressSnapshot = snapshots.First(s => s.DeviceId == "PLC-PRESS-001");

        // Availability = 1.0 (no downtime records)
        pressSnapshot.Availability.Should().Be(1.0);
        // TotalParts = 200 + 300 = 500, TotalRejects = 10 + 5 = 15
        pressSnapshot.TotalPartsProduced.Should().Be(500);
        pressSnapshot.TotalRejects.Should().Be(15);
        pressSnapshot.GoodParts.Should().Be(485);
        // Quality = 485 / 500 = 0.97
        pressSnapshot.Quality.Should().BeApproximately(0.97, 0.001);
        // Performance = TotalParts / (IdealRate × RunTime) — will be > 0
        pressSnapshot.Performance.Should().BeGreaterThan(0.0);
        // OEE = A × P × Q — positive since all factors are > 0
        pressSnapshot.Oee.Should().BeGreaterThan(0.0);
    }

    /// <summary>
    /// When no parts are produced, quality should default to 1.0 (not NaN from 0/0).
    /// </summary>
    [Fact]
    public async Task OeeCalculation_WithZeroProduction_QualityIsOne()
    {
        // Don't seed any production records
        var (hubContext, _) = CreateMockHubContext();
        var scopeFactory = CreateScopeFactory();
        var logger = Substitute.For<ILogger<OeeCalculationService>>();

        var service = new OeeCalculationService(logger, hubContext, scopeFactory);

        var snapshots = await service.CalculateAllOeeAsync();

        foreach (var snapshot in snapshots)
        {
            snapshot.Quality.Should().Be(1.0,
                "with no parts produced, quality should default to 1.0, not NaN");
            double.IsNaN(snapshot.Quality).Should().BeFalse("quality must never be NaN");
        }
    }

    /// <summary>
    /// When all parts are rejected, quality should be 0.0.
    /// </summary>
    [Fact]
    public async Task OeeCalculation_WithAllRejects_QualityIsZero()
    {
        // Create service FIRST so _serviceStartedAt precedes seeded record timestamps.
        var (hubContext, _) = CreateMockHubContext();
        var scopeFactory = CreateScopeFactory();
        var logger = Substitute.For<ILogger<OeeCalculationService>>();

        var service = new OeeCalculationService(logger, hubContext, scopeFactory);

        await using (var ctx = CreateContext())
        {
            ctx.ProductionRecords.Add(new ProductionRecord
            {
                DeviceId = "PLC-CONV-002",
                Timestamp = DateTimeOffset.UtcNow,
                PartsProduced = 100,
                RejectCount = 100,
            });
            await ctx.SaveChangesAsync();
        }

        var snapshots = await service.CalculateAllOeeAsync();
        var convSnapshot = snapshots.First(s => s.DeviceId == "PLC-CONV-002");

        convSnapshot.Quality.Should().Be(0.0, "all parts rejected → quality = 0");
        convSnapshot.Oee.Should().Be(0.0, "zero quality → zero OEE");
    }

    /// <summary>
    /// A device with no downtime records should have availability = 1.0.
    /// </summary>
    [Fact]
    public async Task OeeCalculation_DeviceWithFullUptime_AvailabilityIsOne()
    {
        var (hubContext, _) = CreateMockHubContext();
        var scopeFactory = CreateScopeFactory();
        var logger = Substitute.For<ILogger<OeeCalculationService>>();

        var service = new OeeCalculationService(logger, hubContext, scopeFactory);

        var snapshots = await service.CalculateAllOeeAsync();

        foreach (var snapshot in snapshots)
        {
            snapshot.Availability.Should().Be(1.0,
                "no downtime records → full availability");
            snapshot.DowntimeSeconds.Should().Be(0.0);
        }
    }

    /// <summary>
    /// A device with a resolved downtime record should have availability &lt; 1.0.
    /// </summary>
    [Fact]
    public async Task OeeCalculation_DeviceWithDowntime_ReducesAvailability()
    {
        // Create service FIRST so _serviceStartedAt precedes seeded record timestamps.
        var (hubContext, _) = CreateMockHubContext();
        var scopeFactory = CreateScopeFactory();
        var logger = Substitute.For<ILogger<OeeCalculationService>>();

        var service = new OeeCalculationService(logger, hubContext, scopeFactory);

        // Seed a resolved downtime record for PLC-WELD-003
        await using (var ctx = CreateContext())
        {
            ctx.DowntimeRecords.Add(new DowntimeRecord
            {
                DeviceId = "PLC-WELD-003",
                DeviceName = "Welding PLC",
                StartedAt = DateTimeOffset.UtcNow,
                EndedAt = DateTimeOffset.UtcNow.AddSeconds(2),
                DurationSeconds = 2.0,
                IsResolved = true,
                Reason = "Test downtime",
            });
            await ctx.SaveChangesAsync();
        }

        var snapshots = await service.CalculateAllOeeAsync();
        var weldSnapshot = snapshots.First(s => s.DeviceId == "PLC-WELD-003");

        weldSnapshot.Availability.Should().BeLessThan(1.0,
            "device had downtime → availability < 100%");
        weldSnapshot.DowntimeSeconds.Should().BeGreaterThan(0.0);

        // Other devices without downtime should still be at 1.0
        var pressSnapshot = snapshots.First(s => s.DeviceId == "PLC-PRESS-001");
        pressSnapshot.Availability.Should().Be(1.0);
    }

    // ──────────────────────────────────────────────
    //  Plant OEE Static Method Tests
    // ──────────────────────────────────────────────

    /// <summary>
    /// CalculatePlantOee with no snapshots returns 0.
    /// </summary>
    [Fact]
    public void PlantOee_WithNoSnapshots_ReturnsZero()
    {
        var result = OeeCalculationService.CalculatePlantOee(new List<OeeSnapshot>());

        result.Should().Be(0.0);
    }

    /// <summary>
    /// CalculatePlantOee with production uses weighted average by total parts.
    /// </summary>
    [Fact]
    public void PlantOee_WithProduction_UsesWeightedAverage()
    {
        var snapshots = new List<OeeSnapshot>
        {
            new()
            {
                DeviceId = "D1",
                Availability = 1.0,
                Performance = 1.0,
                Quality = 1.0,
                TotalPartsProduced = 100,
            },
            new()
            {
                DeviceId = "D2",
                Availability = 0.5,
                Performance = 0.5,
                Quality = 0.5,
                TotalPartsProduced = 100,
            },
        };

        var result = OeeCalculationService.CalculatePlantOee(snapshots);

        // D1: OEE=1.0, weight=100; D2: OEE=0.125, weight=100
        // Weighted = (1.0×100 + 0.125×100) / 200 = 112.5/200 = 0.5625
        result.Should().BeApproximately(0.5625, 0.0001);
    }

    /// <summary>
    /// CalculatePlantOee with no production falls back to simple average.
    /// </summary>
    [Fact]
    public void PlantOee_WithNoProduction_UsesSimpleAverage()
    {
        var snapshots = new List<OeeSnapshot>
        {
            new() { DeviceId = "D1", Availability = 1.0, Performance = 0.0, Quality = 1.0, TotalPartsProduced = 0 },
            new() { DeviceId = "D2", Availability = 1.0, Performance = 0.0, Quality = 1.0, TotalPartsProduced = 0 },
        };

        var result = OeeCalculationService.CalculatePlantOee(snapshots);

        // Both have OEE = 1.0 × 0.0 × 1.0 = 0.0 → average = 0.0
        result.Should().Be(0.0);
    }

    // ──────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────

    private static (IHubContext<DashboardHub> hubContext, IClientProxy clientProxy) CreateMockHubContext()
    {
        var clientProxy = Substitute.For<IClientProxy>();
        var clients = Substitute.For<IHubClients>();
        clients.All.Returns(clientProxy);

        var hubContext = Substitute.For<IHubContext<DashboardHub>>();
        hubContext.Clients.Returns(clients);

        return (hubContext, clientProxy);
    }

    private IServiceScopeFactory CreateScopeFactory()
    {
        var services = new ServiceCollection();
        services.AddDbContext<DowntimeDbContext>(o => o.UseSqlite(_connection));
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IServiceScopeFactory>();
    }
}
