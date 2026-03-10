using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NatsPoc.Dashboard.Data;
using NatsPoc.Dashboard.Hubs;
using NatsPoc.Dashboard.Models;
using NatsPoc.Dashboard.Services;
using NatsPoc.Shared;
using NSubstitute;
using Xunit;

namespace NatsPoc.Tests;

/// <summary>
/// Tests for the downtime history feature: DowntimeRecord model, SQLite persistence
/// via DowntimeDbContext, and the service-level behavior that records/resolves downtimes.
///
/// All database tests use in-memory SQLite — no external database required.
/// Service tests construct the real DeviceStatusMonitorService with mocked SignalR
/// and a real DeviceTracker + in-memory SQLite context.
///
/// These tests define the contract that Dallas's implementation must satisfy:
///   - NatsPoc.Dashboard.Models.DowntimeRecord
///   - NatsPoc.Dashboard.Data.DowntimeDbContext
///   - Modified DeviceStatusMonitorService with DowntimeDbContext injection
/// </summary>
public class DowntimeHistoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<DowntimeDbContext> _dbOptions;

    public DowntimeHistoryTests()
    {
        // In-memory SQLite: the DB lives as long as the connection is open.
        // All DbContext instances created from _dbOptions share this connection.
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
    //  DowntimeRecord Model Tests
    // ──────────────────────────────────────────────

    /// <summary>
    /// A freshly created DowntimeRecord should default to unresolved:
    /// IsResolved = false, EndedAt = null, DurationSeconds = null.
    /// </summary>
    [Fact]
    public void DowntimeRecord_NewRecord_HasCorrectDefaults()
    {
        // Arrange & Act
        var record = new DowntimeRecord
        {
            DeviceId = "PLC-001",
            DeviceName = "PLC Simulator 001",
            StartedAt = DateTimeOffset.UtcNow,
            Reason = "No heartbeat received within timeout window"
        };

        // Assert
        record.IsResolved.Should().BeFalse("new downtime records start unresolved");
        record.EndedAt.Should().BeNull("downtime hasn't ended yet");
        record.DurationSeconds.Should().BeNull("no duration until resolved");
        record.DeviceId.Should().Be("PLC-001");
        record.Reason.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// When a downtime is resolved, EndedAt and DurationSeconds should reflect
    /// the actual elapsed time.
    /// </summary>
    [Fact]
    public void DowntimeRecord_WhenResolved_HasDuration()
    {
        // Arrange
        var start = new DateTimeOffset(2026, 3, 10, 10, 0, 0, TimeSpan.Zero);
        var end = start.AddMinutes(5);

        // Act — simulate resolving a downtime
        var record = new DowntimeRecord
        {
            DeviceId = "PLC-002",
            DeviceName = "PLC Simulator 002",
            StartedAt = start,
            EndedAt = end,
            DurationSeconds = (end - start).TotalSeconds,
            Reason = "No heartbeat received within timeout window",
            IsResolved = true
        };

        // Assert
        record.IsResolved.Should().BeTrue();
        record.EndedAt.Should().Be(end);
        record.DurationSeconds.Should().Be(300, "5 minutes = 300 seconds");
    }

    // ──────────────────────────────────────────────
    //  SQLite Database Tests
    // ──────────────────────────────────────────────

    /// <summary>
    /// Basic round-trip: save a DowntimeRecord to SQLite, read it back,
    /// and verify all fields survive persistence.
    /// </summary>
    [Fact]
    public async Task DbContext_CanCreateAndRetrieveDowntime()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var record = new DowntimeRecord
        {
            DeviceId = "PLC-010",
            DeviceName = "Assembly Line PLC",
            StartedAt = now,
            Reason = "Heartbeat timeout exceeded",
            IsResolved = false
        };

        // Act — save with one context, read with another to avoid change tracker
        await using (var writeCtx = CreateContext())
        {
            writeCtx.DowntimeRecords.Add(record);
            await writeCtx.SaveChangesAsync();
        }

        await using var readCtx = CreateContext();
        var loaded = await readCtx.DowntimeRecords
            .FirstOrDefaultAsync(r => r.DeviceId == "PLC-010");

        // Assert
        loaded.Should().NotBeNull("record was saved to the database");
        loaded!.Id.Should().BeGreaterThan(0, "SQLite should auto-generate the Id");
        loaded.DeviceId.Should().Be("PLC-010");
        loaded.DeviceName.Should().Be("Assembly Line PLC");
        loaded.StartedAt.Should().BeCloseTo(now, TimeSpan.FromSeconds(1));
        loaded.Reason.Should().Be("Heartbeat timeout exceeded");
        loaded.IsResolved.Should().BeFalse();
        loaded.EndedAt.Should().BeNull();
        loaded.DurationSeconds.Should().BeNull();
    }

    /// <summary>
    /// Multiple devices have downtime records. Querying by DeviceId should
    /// return only records for that specific device.
    /// </summary>
    [Fact]
    public async Task DbContext_CanQueryByDeviceId()
    {
        // Arrange — 3 records across 2 devices
        var now = DateTimeOffset.UtcNow;
        await using (var ctx = CreateContext())
        {
            ctx.DowntimeRecords.AddRange(
                new DowntimeRecord { DeviceId = "PLC-A", DeviceName = "A", StartedAt = now, Reason = "Timeout" },
                new DowntimeRecord { DeviceId = "PLC-B", DeviceName = "B", StartedAt = now, Reason = "Timeout" },
                new DowntimeRecord { DeviceId = "PLC-A", DeviceName = "A", StartedAt = now.AddMinutes(-30), Reason = "Earlier timeout" }
            );
            await ctx.SaveChangesAsync();
        }

        // Act
        await using var readCtx = CreateContext();
        var plcARecords = await readCtx.DowntimeRecords
            .Where(r => r.DeviceId == "PLC-A")
            .ToListAsync();

        // Assert
        plcARecords.Should().HaveCount(2, "PLC-A has 2 downtime records");
        plcARecords.Should().AllSatisfy(r => r.DeviceId.Should().Be("PLC-A"));
    }

    /// <summary>
    /// Create an unresolved downtime, then update it to resolved.
    /// Verify EndedAt, DurationSeconds, and IsResolved are set correctly.
    /// </summary>
    [Fact]
    public async Task DbContext_CanResolveDowntime()
    {
        // Arrange — create an unresolved downtime
        var start = DateTimeOffset.UtcNow;
        int recordId;
        await using (var ctx = CreateContext())
        {
            var record = new DowntimeRecord
            {
                DeviceId = "PLC-020",
                DeviceName = "Packaging PLC",
                StartedAt = start,
                Reason = "Network interruption",
                IsResolved = false
            };
            ctx.DowntimeRecords.Add(record);
            await ctx.SaveChangesAsync();
            recordId = record.Id;
        }

        // Act — resolve it (simulating what the monitor service does)
        var end = start.AddMinutes(3);
        await using (var ctx = CreateContext())
        {
            var openRecord = await ctx.DowntimeRecords
                .FirstAsync(r => r.Id == recordId);
            openRecord.EndedAt = end;
            openRecord.DurationSeconds = (end - openRecord.StartedAt).TotalSeconds;
            openRecord.IsResolved = true;
            await ctx.SaveChangesAsync();
        }

        // Assert — read back with a fresh context
        await using var verifyCtx = CreateContext();
        var resolved = await verifyCtx.DowntimeRecords.FirstAsync(r => r.Id == recordId);
        resolved.IsResolved.Should().BeTrue();
        resolved.EndedAt.Should().NotBeNull();
        resolved.DurationSeconds.Should().BeApproximately(180, 1, "3 minutes ≈ 180 seconds");
    }

    /// <summary>
    /// The API returns records ordered by StartedAt descending (most recent first).
    /// Verify that EF Core + SQLite supports this ordering.
    /// </summary>
    [Fact]
    public async Task DbContext_OrdersByStartedAtDescending()
    {
        // Arrange — insert records out of chronological order
        var baseTime = DateTimeOffset.UtcNow;
        await using (var ctx = CreateContext())
        {
            ctx.DowntimeRecords.AddRange(
                new DowntimeRecord { DeviceId = "PLC-X", DeviceName = "X", StartedAt = baseTime.AddMinutes(-10), Reason = "Oldest" },
                new DowntimeRecord { DeviceId = "PLC-X", DeviceName = "X", StartedAt = baseTime, Reason = "Newest" },
                new DowntimeRecord { DeviceId = "PLC-X", DeviceName = "X", StartedAt = baseTime.AddMinutes(-5), Reason = "Middle" }
            );
            await ctx.SaveChangesAsync();
        }

        // Act — query and order client-side (SQLite does not support ORDER BY
        //        on DateTimeOffset columns; production code must also sort in-memory).
        await using var readCtx = CreateContext();
        var ordered = (await readCtx.DowntimeRecords.ToListAsync())
            .OrderByDescending(r => r.StartedAt)
            .ToList();

        // Assert
        ordered.Should().HaveCount(3);
        ordered[0].Reason.Should().Be("Newest");
        ordered[1].Reason.Should().Be("Middle");
        ordered[2].Reason.Should().Be("Oldest");
    }

    // ──────────────────────────────────────────────
    //  Service Integration Tests
    //
    //  These construct the real DeviceStatusMonitorService with:
    //  - Real DeviceTracker (pure domain logic, no external deps)
    //  - Real in-memory SQLite via DowntimeDbContext (behind IServiceScopeFactory)
    //  - Mocked IHubContext<DashboardHub> (SignalR)
    //  - Real IConfiguration (in-memory provider)
    //  - Mocked ILogger
    //
    //  The service uses IServiceScopeFactory to resolve DowntimeDbContext per-operation.
    //  The 2-second timer means we need ~4s wait for assertions.
    //
    //  IMPORTANT: Use a large tracker timeout (30s) so that once a device recovers,
    //  it stays UP for the remainder of the test. A short timeout (2s) causes the
    //  recovery heartbeat to time out again, creating unwanted extra downtime records.
    // ──────────────────────────────────────────────

    /// <summary>
    /// When DeviceStatusMonitorService detects a device has gone OFFLINE
    /// (heartbeat timeout exceeded), it should create an unresolved
    /// DowntimeRecord in the database with the device info and reason.
    /// </summary>
    [Fact]
    public async Task Monitor_WhenDeviceGoesOffline_CreatesDowntimeRecord()
    {
        // Arrange — device heartbeat was 60s ago, tracker timeout 30s → device is DOWN
        var tracker = new DeviceTracker(TimeSpan.FromSeconds(30));
        tracker.RecordHeartbeat("PLC-OFFLINE-1", DateTimeOffset.UtcNow.AddSeconds(-60));

        var (hubContext, clientProxy) = CreateMockHubContext();
        var scopeFactory = CreateScopeFactory();
        var config = CreateConfig(timeoutSeconds: 30);
        var logger = Substitute.For<ILogger<DeviceStatusMonitorService>>();

        var service = new DeviceStatusMonitorService(logger, tracker, hubContext, scopeFactory, config);

        // Act — run the service long enough for at least one timer tick (2s interval)
        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await Task.Delay(4000);
        cts.Cancel();
        try { await service.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

        // Assert — a downtime record should exist for this device
        await using var verifyCtx = CreateContext();
        var records = await verifyCtx.DowntimeRecords
            .Where(r => r.DeviceId == "PLC-OFFLINE-1")
            .ToListAsync();

        records.Should().HaveCount(1, "one downtime should be recorded for the offline device");
        records[0].IsResolved.Should().BeFalse("device is still offline");
        records[0].EndedAt.Should().BeNull();
        records[0].Reason.Should().NotBeNullOrEmpty("a reason should explain why the device is down");

        // Verify SignalR notification was sent
        await clientProxy.Received().SendCoreAsync(
            "ReceiveDowntimeStarted",
            Arg.Any<object?[]>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// When a device comes back ONLINE after being down, the service should
    /// find the open DowntimeRecord and resolve it: set EndedAt, compute
    /// DurationSeconds, and mark IsResolved = true.
    /// </summary>
    [Fact]
    public async Task Monitor_WhenDeviceComesBackOnline_ResolvesDowntimeRecord()
    {
        // Arrange — device is DOWN (heartbeat 60s ago, 30s tracker timeout)
        var tracker = new DeviceTracker(TimeSpan.FromSeconds(30));
        tracker.RecordHeartbeat("PLC-RECOVER-1", DateTimeOffset.UtcNow.AddSeconds(-60));

        var (hubContext, clientProxy) = CreateMockHubContext();
        var scopeFactory = CreateScopeFactory();
        var config = CreateConfig(timeoutSeconds: 30);
        var logger = Substitute.For<ILogger<DeviceStatusMonitorService>>();

        var service = new DeviceStatusMonitorService(logger, tracker, hubContext, scopeFactory, config);

        // Act Phase 1 — let the service detect the offline state
        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await Task.Delay(4000);

        // Verify Phase 1 — downtime was recorded
        await using (var midCtx = CreateContext())
        {
            var openRecords = await midCtx.DowntimeRecords
                .Where(r => r.DeviceId == "PLC-RECOVER-1" && !r.IsResolved)
                .ToListAsync();
            openRecords.Should().HaveCount(1, "device should have one open downtime");
        }

        // Act Phase 2 — device sends a new heartbeat (comes back online)
        // With 30s tracker timeout, this heartbeat keeps the device UP for the test duration
        tracker.RecordHeartbeat("PLC-RECOVER-1", DateTimeOffset.UtcNow);
        await Task.Delay(4000); // wait for next timer tick to detect recovery

        cts.Cancel();
        try { await service.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

        // Assert — the downtime record should now be resolved
        await using var verifyCtx = CreateContext();
        var resolved = await verifyCtx.DowntimeRecords
            .Where(r => r.DeviceId == "PLC-RECOVER-1")
            .ToListAsync();

        resolved.Should().HaveCount(1);
        resolved[0].IsResolved.Should().BeTrue("device came back online");
        resolved[0].EndedAt.Should().NotBeNull("resolution time should be recorded");
        resolved[0].DurationSeconds.Should().BeGreaterThan(0, "downtime lasted some duration");

        // Verify SignalR resolved notification was sent
        await clientProxy.Received().SendCoreAsync(
            "ReceiveDowntimeResolved",
            Arg.Any<object?[]>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Two devices go down independently. Each should get its own separate
    /// DowntimeRecord — one device's recovery shouldn't affect the other.
    /// </summary>
    [Fact]
    public async Task Monitor_MultipleDevices_TracksEachIndependently()
    {
        // Arrange — both devices are DOWN (60s since last heartbeat, 30s timeout)
        var tracker = new DeviceTracker(TimeSpan.FromSeconds(30));
        tracker.RecordHeartbeat("PLC-MULTI-A", DateTimeOffset.UtcNow.AddSeconds(-60));
        tracker.RecordHeartbeat("PLC-MULTI-B", DateTimeOffset.UtcNow.AddSeconds(-60));

        var (hubContext, clientProxy) = CreateMockHubContext();
        var scopeFactory = CreateScopeFactory();
        var config = CreateConfig(timeoutSeconds: 30);
        var logger = Substitute.For<ILogger<DeviceStatusMonitorService>>();

        var service = new DeviceStatusMonitorService(logger, tracker, hubContext, scopeFactory, config);

        // Act Phase 1 — both devices detected as offline
        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await Task.Delay(4000);

        // Phase 2 — only PLC-MULTI-A recovers (stays UP with 30s timeout)
        tracker.RecordHeartbeat("PLC-MULTI-A", DateTimeOffset.UtcNow);
        await Task.Delay(4000);

        cts.Cancel();
        try { await service.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

        // Assert
        await using var verifyCtx = CreateContext();
        var allRecords = await verifyCtx.DowntimeRecords
            .Where(r => r.DeviceId == "PLC-MULTI-A" || r.DeviceId == "PLC-MULTI-B")
            .ToListAsync();

        allRecords.Should().HaveCount(2, "each device gets its own downtime record");

        var recordA = allRecords.First(r => r.DeviceId == "PLC-MULTI-A");
        recordA.IsResolved.Should().BeTrue("PLC-MULTI-A recovered");
        recordA.EndedAt.Should().NotBeNull();

        var recordB = allRecords.First(r => r.DeviceId == "PLC-MULTI-B");
        recordB.IsResolved.Should().BeFalse("PLC-MULTI-B is still down");
        recordB.EndedAt.Should().BeNull();
    }

    // ──────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────

    /// <summary>
    /// Creates a mocked IHubContext with IHubClients and IClientProxy wired up.
    /// Returns both so tests can verify SignalR calls if needed.
    /// </summary>
    private static (IHubContext<DashboardHub> hubContext, IClientProxy clientProxy) CreateMockHubContext()
    {
        var clientProxy = Substitute.For<IClientProxy>();
        var clients = Substitute.For<IHubClients>();
        clients.All.Returns(clientProxy);

        var hubContext = Substitute.For<IHubContext<DashboardHub>>();
        hubContext.Clients.Returns(clients);

        return (hubContext, clientProxy);
    }

    /// <summary>
    /// Creates a real IServiceScopeFactory using a ServiceCollection so that
    /// scoped DowntimeDbContext instances use the shared in-memory SQLite connection.
    /// This avoids mock-related issues with IServiceProvider.GetRequiredService.
    /// </summary>
    private IServiceScopeFactory CreateScopeFactory()
    {
        var services = new ServiceCollection();
        services.AddDbContext<DowntimeDbContext>(o => o.UseSqlite(_connection));
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IServiceScopeFactory>();
    }

    /// <summary>
    /// Creates a real IConfiguration with the detector timeout setting.
    /// </summary>
    private static IConfiguration CreateConfig(int timeoutSeconds = 5)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Detector:TimeoutSeconds"] = timeoutSeconds.ToString()
            })
            .Build();
    }
}
