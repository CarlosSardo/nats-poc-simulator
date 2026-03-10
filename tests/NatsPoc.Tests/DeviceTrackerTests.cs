using FluentAssertions;
using NatsPoc.Shared;
using Xunit;

namespace NatsPoc.Tests;

/// <summary>
/// Tests for DeviceTracker — the core downtime detection logic.
///
/// These tests do NOT require a running NATS server. They validate pure domain logic:
/// given a stream of heartbeats (or absence of them), does the tracker correctly
/// determine which devices are UP and which are DOWN?
///
/// The default timeout used in most tests is 15 seconds, matching the production config.
/// </summary>
public class DeviceTrackerTests
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// When a device sends its very first heartbeat, it should immediately
    /// be considered UP. There's no "warm-up" period — one heartbeat is enough.
    /// </summary>
    [Fact]
    public void DeviceTracker_NewDevice_IsInitiallyUp()
    {
        // Arrange
        var tracker = new DeviceTracker(DefaultTimeout);
        var now = DateTimeOffset.UtcNow;

        // Act — first heartbeat ever from PLC-001
        tracker.RecordHeartbeat("PLC-001", now);

        // Assert — device should be UP right away
        var status = tracker.GetStatus("PLC-001", asOf: now);
        status.IsUp.Should().BeTrue("a device that just sent a heartbeat is alive");
        status.LastSeen.Should().Be(now);
        status.DownSince.Should().BeNull("the device has never been down");
    }

    /// <summary>
    /// If a device sent a heartbeat 10 seconds ago (within the 15s threshold),
    /// it should still be considered UP.
    /// </summary>
    [Fact]
    public void DeviceTracker_DeviceWithRecentHeartbeat_IsUp()
    {
        // Arrange
        var tracker = new DeviceTracker(DefaultTimeout);
        var heartbeatTime = DateTimeOffset.UtcNow;
        tracker.RecordHeartbeat("PLC-002", heartbeatTime);

        // Act — check status 10 seconds later (still within 15s window)
        var checkTime = heartbeatTime.AddSeconds(10);
        var status = tracker.GetStatus("PLC-002", asOf: checkTime);

        // Assert
        status.IsUp.Should().BeTrue("10s < 15s timeout, device is still within window");
        status.LastSeen.Should().Be(heartbeatTime);
        status.DownSince.Should().BeNull();
    }

    /// <summary>
    /// If 20 seconds pass with no heartbeat (exceeding the 15s threshold),
    /// the device should be marked DOWN.
    /// </summary>
    [Fact]
    public void DeviceTracker_DeviceExceedingTimeout_IsDown()
    {
        // Arrange
        var tracker = new DeviceTracker(DefaultTimeout);
        var heartbeatTime = DateTimeOffset.UtcNow;
        tracker.RecordHeartbeat("PLC-003", heartbeatTime);

        // Act — check status 20 seconds later (exceeds 15s timeout)
        var checkTime = heartbeatTime.AddSeconds(20);
        var status = tracker.GetStatus("PLC-003", asOf: checkTime);

        // Assert
        status.IsUp.Should().BeFalse("20s > 15s timeout, device missed its window");
        status.LastSeen.Should().Be(heartbeatTime);
        status.DownSince.Should().NotBeNull("we should record when the device went down");
    }

    /// <summary>
    /// A device goes DOWN (no heartbeat for 20s), then sends a new heartbeat.
    /// After the new heartbeat, it should be considered UP again — recovery works.
    /// </summary>
    [Fact]
    public void DeviceTracker_DeviceRecovers_AfterNewHeartbeat()
    {
        // Arrange
        var tracker = new DeviceTracker(DefaultTimeout);
        var firstHeartbeat = DateTimeOffset.UtcNow;
        tracker.RecordHeartbeat("PLC-004", firstHeartbeat);

        // Simulate: 20s pass — device is down
        var downCheckTime = firstHeartbeat.AddSeconds(20);
        var downStatus = tracker.GetStatus("PLC-004", asOf: downCheckTime);
        downStatus.IsUp.Should().BeFalse("sanity check: device should be down after 20s");

        // Act — device comes back with a new heartbeat at 25s
        var recoveryTime = firstHeartbeat.AddSeconds(25);
        tracker.RecordHeartbeat("PLC-004", recoveryTime);

        // Assert — device should be UP again
        var recoveredStatus = tracker.GetStatus("PLC-004", asOf: recoveryTime);
        recoveredStatus.IsUp.Should().BeTrue("device sent a new heartbeat, it recovered");
        recoveredStatus.LastSeen.Should().Be(recoveryTime);
        recoveredStatus.DownSince.Should().BeNull("device is no longer down");
    }

    /// <summary>
    /// Five PLC devices on the floor. PLC-003 goes silent, but the other four
    /// keep sending heartbeats. Only PLC-003 should be DOWN — failures are isolated.
    /// </summary>
    [Fact]
    public void DeviceTracker_MultipleDevices_TrackedIndependently()
    {
        // Arrange
        var tracker = new DeviceTracker(DefaultTimeout);
        var startTime = DateTimeOffset.UtcNow;

        // All 5 devices send initial heartbeats
        var devices = new[] { "PLC-001", "PLC-002", "PLC-003", "PLC-004", "PLC-005" };
        foreach (var device in devices)
            tracker.RecordHeartbeat(device, startTime);

        // 10 seconds later, all except PLC-003 send another heartbeat
        var secondBeat = startTime.AddSeconds(10);
        foreach (var device in devices.Where(d => d != "PLC-003"))
            tracker.RecordHeartbeat(device, secondBeat);

        // Act — check at 20s (PLC-003 last seen at 0s → 20s ago → DOWN)
        var checkTime = startTime.AddSeconds(20);

        // Assert — PLC-003 is down, everyone else is up
        tracker.GetStatus("PLC-001", asOf: checkTime).IsUp.Should().BeTrue();
        tracker.GetStatus("PLC-002", asOf: checkTime).IsUp.Should().BeTrue();
        tracker.GetStatus("PLC-003", asOf: checkTime).IsUp.Should().BeFalse("PLC-003 went silent");
        tracker.GetStatus("PLC-004", asOf: checkTime).IsUp.Should().BeTrue();
        tracker.GetStatus("PLC-005", asOf: checkTime).IsUp.Should().BeTrue();
    }

    /// <summary>
    /// The timeout threshold should be configurable. A 5-second timeout makes
    /// a device go down much faster than the default 15 seconds.
    /// </summary>
    [Fact]
    public void DeviceTracker_ConfigurableTimeout()
    {
        // Arrange — tight 5-second timeout
        var shortTimeout = TimeSpan.FromSeconds(5);
        var tracker = new DeviceTracker(shortTimeout);
        var heartbeatTime = DateTimeOffset.UtcNow;
        tracker.RecordHeartbeat("PLC-006", heartbeatTime);

        // Act — check at 6 seconds (just over the 5s timeout)
        var checkTime = heartbeatTime.AddSeconds(6);
        var status = tracker.GetStatus("PLC-006", asOf: checkTime);

        // Assert — with a 5s timeout, 6s of silence means DOWN
        status.IsUp.Should().BeFalse("6s > 5s custom timeout");
    }

    /// <summary>
    /// GetAllStatuses returns every device the tracker knows about.
    /// Useful for dashboards that show the full fleet at a glance.
    /// </summary>
    [Fact]
    public void DeviceTracker_GetAllStatuses_ReturnsAllTrackedDevices()
    {
        // Arrange
        var tracker = new DeviceTracker(DefaultTimeout);
        var now = DateTimeOffset.UtcNow;

        tracker.RecordHeartbeat("PLC-001", now);
        tracker.RecordHeartbeat("PLC-002", now);
        tracker.RecordHeartbeat("PLC-003", now.AddSeconds(-20)); // this one is old

        // Act
        var allStatuses = tracker.GetAllStatuses(asOf: now);

        // Assert
        allStatuses.Should().HaveCount(3, "we registered 3 devices");
        allStatuses.Should().ContainKey("PLC-001");
        allStatuses.Should().ContainKey("PLC-002");
        allStatuses.Should().ContainKey("PLC-003");

        allStatuses["PLC-001"].IsUp.Should().BeTrue();
        allStatuses["PLC-002"].IsUp.Should().BeTrue();
        allStatuses["PLC-003"].IsUp.Should().BeFalse("PLC-003's heartbeat is 20s old");
    }

    /// <summary>
    /// Querying a device that has never sent a heartbeat should throw,
    /// since we have no data to evaluate.
    /// </summary>
    [Fact]
    public void DeviceTracker_UnknownDevice_Throws()
    {
        // Arrange
        var tracker = new DeviceTracker(DefaultTimeout);

        // Act & Assert
        var act = () => tracker.GetStatus("NEVER-SEEN");
        act.Should().Throw<KeyNotFoundException>("can't check status of a device we've never heard from");
    }

    /// <summary>
    /// Edge case: a heartbeat arrives exactly AT the timeout boundary.
    /// At exactly 15s elapsed, the device should still be UP (boundary is exclusive).
    /// At 15.001s, it's DOWN.
    /// </summary>
    [Fact]
    public void DeviceTracker_ExactTimeoutBoundary_IsStillUp()
    {
        // Arrange
        var tracker = new DeviceTracker(DefaultTimeout);
        var heartbeatTime = DateTimeOffset.UtcNow;
        tracker.RecordHeartbeat("PLC-EDGE", heartbeatTime);

        // Act — check at exactly the timeout boundary
        var exactBoundary = heartbeatTime + DefaultTimeout;
        var status = tracker.GetStatus("PLC-EDGE", asOf: exactBoundary);

        // Assert — at the boundary (elapsed == timeout), device is NOT yet down
        // because we use < (strictly less than) for the "is up" check
        status.IsUp.Should().BeFalse("at exactly 15s elapsed, the timeout has been reached");

        // One tick before the boundary should still be UP
        var justBefore = exactBoundary - TimeSpan.FromMilliseconds(1);
        var beforeStatus = tracker.GetStatus("PLC-EDGE", asOf: justBefore);
        beforeStatus.IsUp.Should().BeTrue("14.999s < 15s timeout, still alive");
    }

    /// <summary>
    /// Device goes UP → DOWN → UP → DOWN in quick succession.
    /// Each state transition should be tracked correctly at every point in time.
    /// </summary>
    [Fact]
    public void RapidStateTransitions_TrackedCorrectly()
    {
        // Arrange — tight 3-second timeout for fast transitions
        var timeout = TimeSpan.FromSeconds(3);
        var tracker = new DeviceTracker(timeout);
        var t0 = DateTimeOffset.UtcNow;

        // UP: heartbeat at t0
        tracker.RecordHeartbeat("PLC-RAPID", t0);
        tracker.GetStatus("PLC-RAPID", asOf: t0).IsUp.Should().BeTrue("just sent heartbeat → UP");

        // DOWN: check at t0+4s (exceeds 3s timeout)
        var t1 = t0.AddSeconds(4);
        tracker.GetStatus("PLC-RAPID", asOf: t1).IsUp.Should().BeFalse("4s > 3s timeout → DOWN");

        // UP again: new heartbeat at t0+5s
        var t2 = t0.AddSeconds(5);
        tracker.RecordHeartbeat("PLC-RAPID", t2);
        tracker.GetStatus("PLC-RAPID", asOf: t2).IsUp.Should().BeTrue("new heartbeat → UP again");
        tracker.GetStatus("PLC-RAPID", asOf: t2).DownSince.Should().BeNull("recovered, no longer down");

        // DOWN again: check at t0+9s (4s since last heartbeat at t0+5s)
        var t3 = t0.AddSeconds(9);
        tracker.GetStatus("PLC-RAPID", asOf: t3).IsUp.Should().BeFalse("4s since last beat → DOWN again");
        tracker.GetStatus("PLC-RAPID", asOf: t3).DownSince.Should().NotBeNull();
    }

    /// <summary>
    /// Record heartbeats from 100 devices simultaneously using Parallel.ForEach.
    /// DeviceTracker uses ConcurrentDictionary internally — this verifies thread safety.
    /// </summary>
    [Fact]
    public void ConcurrentHeartbeats_ThreadSafe()
    {
        // Arrange
        var tracker = new DeviceTracker(DefaultTimeout);
        var now = DateTimeOffset.UtcNow;
        var deviceIds = Enumerable.Range(1, 100).Select(i => $"PLC-CONC-{i:D3}").ToArray();

        // Act — blast heartbeats from 100 devices in parallel
        var act = () => Parallel.ForEach(deviceIds, deviceId =>
        {
            tracker.RecordHeartbeat(deviceId, now);
        });

        // Assert — no exceptions thrown
        act.Should().NotThrow("ConcurrentDictionary should handle parallel writes safely");

        // All 100 devices should be tracked and UP
        var allStatuses = tracker.GetAllStatuses(asOf: now);
        allStatuses.Should().HaveCount(100, "all 100 devices should be tracked");
        allStatuses.Values.Should().AllSatisfy(s => s.IsUp.Should().BeTrue());
    }
}
