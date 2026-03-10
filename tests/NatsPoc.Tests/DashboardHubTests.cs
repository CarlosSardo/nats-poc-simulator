using System.Collections;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NatsPoc.Dashboard.Data;
using NatsPoc.Dashboard.Hubs;
using NatsPoc.Shared;
using NSubstitute;
using Xunit;

namespace NatsPoc.Tests;

/// <summary>
/// Tests for DashboardHub — the SignalR hub that sends device statuses to connected clients.
///
/// These tests do NOT require a running SignalR server or NATS connection.
/// They verify the hub's behavior using mocked SignalR infrastructure:
/// - On connect, the hub should send all current device statuses to the caller.
/// - The hub reads from DeviceTracker (shared singleton) to get device data.
///
/// Note: The hub serializes device data into anonymous objects (with string timestamps),
/// so assertions use IDictionary to inspect the payload shape.
/// </summary>
public class DashboardHubTests
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task OnConnectedAsync_SendsAllStatuses_ToCallerOnly()
    {
        // Arrange
        var tracker = new DeviceTracker(DefaultTimeout);
        var now = DateTimeOffset.UtcNow;
        tracker.RecordHeartbeat("PLC-001", now);
        tracker.RecordHeartbeat("PLC-002", now);

        var hub = CreateHubWithMockedClients(tracker, out var callerProxy);

        // Act
        await hub.OnConnectedAsync();

        // Assert
        await callerProxy.Received(1).SendCoreAsync(
            "ReceiveAllStatuses",
            Arg.Any<object?[]>(),
            Arg.Any<CancellationToken>());

        var payload = CapturePayload(callerProxy);
        payload.Keys.Cast<string>().Should().BeEquivalentTo("PLC-001", "PLC-002");
    }

    [Fact]
    public async Task OnConnectedAsync_WithNoDevices_SendsEmptyStatuses()
    {
        // Arrange
        var tracker = new DeviceTracker(DefaultTimeout);
        var hub = CreateHubWithMockedClients(tracker, out var callerProxy);

        // Act
        await hub.OnConnectedAsync();

        // Assert
        await callerProxy.Received(1).SendCoreAsync(
            "ReceiveAllStatuses",
            Arg.Any<object?[]>(),
            Arg.Any<CancellationToken>());

        var payload = CapturePayload(callerProxy);
        payload.Count.Should().Be(0);
    }

    [Fact]
    public async Task OnConnectedAsync_WithMultipleDevices_SendsAllDeviceData()
    {
        // Arrange — 3 devices, one of which has an old heartbeat (will be DOWN)
        var tracker = new DeviceTracker(DefaultTimeout);
        var now = DateTimeOffset.UtcNow;
        tracker.RecordHeartbeat("PLC-001", now);                    // UP
        tracker.RecordHeartbeat("PLC-002", now);                    // UP
        tracker.RecordHeartbeat("PLC-003", now.AddSeconds(-20));    // DOWN (20s old)

        var hub = CreateHubWithMockedClients(tracker, out var callerProxy);

        // Act
        await hub.OnConnectedAsync();

        // Assert — all 3 devices present in payload
        var payload = CapturePayload(callerProxy);
        payload.Count.Should().Be(3);
        payload.Keys.Cast<string>().Should().Contain("PLC-001")
            .And.Contain("PLC-002")
            .And.Contain("PLC-003");

        // Verify IsUp via reflection on the anonymous objects
        GetAnonymousProp<bool>(payload["PLC-001"]!, "IsUp").Should().BeTrue();
        GetAnonymousProp<bool>(payload["PLC-002"]!, "IsUp").Should().BeTrue();
        GetAnonymousProp<bool>(payload["PLC-003"]!, "IsUp").Should().BeFalse();
    }

    [Fact]
    public async Task OnConnectedAsync_StatusesContainCorrectTimestamps()
    {
        // Arrange
        var tracker = new DeviceTracker(DefaultTimeout);
        var t1 = new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 3, 10, 12, 0, 5, TimeSpan.Zero);
        tracker.RecordHeartbeat("PLC-A", t1);
        tracker.RecordHeartbeat("PLC-B", t2);

        var hub = CreateHubWithMockedClients(tracker, out var callerProxy);

        // Act
        await hub.OnConnectedAsync();

        // Assert — verify timestamps round-trip through ISO 8601 format
        var payload = CapturePayload(callerProxy);
        GetAnonymousProp<string>(payload["PLC-A"]!, "LastSeen").Should().Be(t1.ToString("o"));
        GetAnonymousProp<string>(payload["PLC-B"]!, "LastSeen").Should().Be(t2.ToString("o"));
    }

    [Fact]
    public async Task OnConnectedAsync_CompletesWithoutException()
    {
        // Arrange
        var tracker = new DeviceTracker(DefaultTimeout);
        var hub = CreateHubWithMockedClients(tracker, out _);

        // Act & Assert — should not throw
        var act = () => hub.OnConnectedAsync();
        await act.Should().NotThrowAsync();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static DashboardHub CreateHubWithMockedClients(
        DeviceTracker tracker,
        out ISingleClientProxy callerProxy)
    {
        callerProxy = Substitute.For<ISingleClientProxy>();

        var mockClients = Substitute.For<IHubCallerClients>();
        mockClients.Caller.Returns(callerProxy);

        // DashboardHub now requires DowntimeDbContext for sending history on connect
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<DowntimeDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new DowntimeDbContext(options);
        db.Database.EnsureCreated();

        var hub = new DashboardHub(tracker, db) { Clients = mockClients };
        return hub;
    }

    /// <summary>
    /// Captures the dictionary payload from the single SendCoreAsync call.
    /// The hub sends a Dictionary&lt;string, anonymous&gt; which implements IDictionary.
    /// </summary>
    private static IDictionary CapturePayload(ISingleClientProxy callerProxy)
    {
        var call = callerProxy.ReceivedCalls()
            .Single(c => (string)c.GetArguments()[0]! == "ReceiveAllStatuses");
        var args = (object?[])call.GetArguments()[1]!;
        args.Should().HaveCount(1);
        return (IDictionary)args[0]!;
    }

    /// <summary>
    /// Reads a property from an anonymous-type object via reflection.
    /// </summary>
    private static T GetAnonymousProp<T>(object obj, string propertyName)
    {
        var prop = obj.GetType().GetProperty(propertyName);
        prop.Should().NotBeNull($"expected property '{propertyName}' on {obj.GetType().Name}");
        return (T)prop!.GetValue(obj)!;
    }
}
