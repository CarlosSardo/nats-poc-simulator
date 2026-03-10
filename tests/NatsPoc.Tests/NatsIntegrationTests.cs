using FluentAssertions;
using NATS.Client.Core;
using NATS.Client.Serializers.Json;
using NatsPoc.Shared;
using NatsPoc.Shared.Models;
using Xunit;

namespace NatsPoc.Tests;

/// <summary>
/// Integration tests that validate NATS pub/sub and serialization end-to-end.
/// These require a running NATS broker (docker-compose up -d).
///
/// Run with: dotnet test --filter "Category=Integration"
/// Skip with: dotnet test --filter "Category!=Integration"
/// </summary>
[Trait("Category", "Integration")]
public class NatsIntegrationTests
{
    private const string NatsUrl = "nats://localhost:4222";

    /// <summary>
    /// Publish a PlcHeartbeat to a device-specific subject, subscribe to the same
    /// subject, and verify the received message matches what was sent.
    /// </summary>
    [Fact]
    public async Task PublishAndSubscribe_HeartbeatRoundTrip()
    {
        // Arrange — unique subject per test run to avoid cross-test interference
        var deviceId = $"RT-{Guid.NewGuid():N}";
        var subject = NatsSubjects.ForDevice(deviceId);
        var serializer = NatsJsonSerializer<PlcHeartbeat>.Default;

        var sent = new PlcHeartbeat
        {
            DeviceId = deviceId,
            Temperature = 72.5,
            Pressure = 1013.25,
            IsRunning = true,
            Timestamp = DateTimeOffset.UtcNow
        };

        await using var nats = new NatsConnection(new NatsOpts { Url = NatsUrl });
        await nats.ConnectAsync();

        // Act — subscribe first, then publish
        PlcHeartbeat? received = null;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var subscribeTask = Task.Run(async () =>
        {
            await foreach (var msg in nats.SubscribeAsync<PlcHeartbeat>(subject, serializer: serializer, cancellationToken: cts.Token))
            {
                received = msg.Data;
                break;
            }
        }, cts.Token);

        // Small delay to ensure subscription is active before publishing
        await Task.Delay(200);
        await nats.PublishAsync(subject, sent, serializer: serializer);

        await subscribeTask;

        // Assert
        received.Should().NotBeNull("we should have received the published heartbeat");
        received!.DeviceId.Should().Be(sent.DeviceId);
        received.Temperature.Should().Be(sent.Temperature);
        received.Pressure.Should().Be(sent.Pressure);
        received.IsRunning.Should().Be(sent.IsRunning);
    }

    /// <summary>
    /// Publish heartbeats from 3 different devices, subscribe with the wildcard
    /// subject (plc.*.heartbeat), and verify all 3 are received.
    /// </summary>
    [Fact]
    public async Task WildcardSubscription_ReceivesFromMultipleDevices()
    {
        // Arrange — 3 unique device IDs
        var testId = Guid.NewGuid().ToString("N")[..8];
        var deviceIds = new[] { $"WC-{testId}-A", $"WC-{testId}-B", $"WC-{testId}-C" };
        var serializer = NatsJsonSerializer<PlcHeartbeat>.Default;

        await using var nats = new NatsConnection(new NatsOpts { Url = NatsUrl });
        await nats.ConnectAsync();

        // Act — subscribe to wildcard, collect messages
        var receivedIds = new List<string>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var deviceIdSet = new HashSet<string>(deviceIds);
        var subscribeTask = Task.Run(async () =>
        {
            await foreach (var msg in nats.SubscribeAsync<PlcHeartbeat>(NatsSubjects.AllHeartbeats, serializer: serializer, cancellationToken: cts.Token))
            {
                if (msg.Data is not null && deviceIdSet.Contains(msg.Data.DeviceId))
                    receivedIds.Add(msg.Data.DeviceId);

                if (receivedIds.Count >= 3)
                    break;
            }
        }, cts.Token);

        await Task.Delay(200);

        // Publish from each device
        foreach (var id in deviceIds)
        {
            var heartbeat = new PlcHeartbeat
            {
                DeviceId = id,
                Temperature = 60.0,
                Pressure = 1000.0,
                IsRunning = true
            };
            await nats.PublishAsync(NatsSubjects.ForDevice(id), heartbeat, serializer: serializer);
        }

        await subscribeTask;

        // Assert
        receivedIds.Should().HaveCount(3, "we published 3 heartbeats on matching subjects");
        receivedIds.Should().BeEquivalentTo(deviceIds, "all 3 device IDs should be received");
    }

    /// <summary>
    /// Verify that all PlcHeartbeat fields survive JSON serialization over NATS —
    /// DeviceId, Temperature, Pressure, IsRunning, and Timestamp.
    /// </summary>
    [Fact]
    public async Task JsonSerialization_RoundTrip()
    {
        // Arrange — heartbeat with specific, verifiable values
        var deviceId = $"JSON-{Guid.NewGuid():N}";
        var subject = NatsSubjects.ForDevice(deviceId);
        var serializer = NatsJsonSerializer<PlcHeartbeat>.Default;
        var timestamp = new DateTimeOffset(2026, 3, 9, 12, 0, 0, TimeSpan.Zero);

        var sent = new PlcHeartbeat
        {
            DeviceId = deviceId,
            Temperature = 99.99,
            Pressure = 750.123,
            IsRunning = false,
            Timestamp = timestamp
        };

        await using var nats = new NatsConnection(new NatsOpts { Url = NatsUrl });
        await nats.ConnectAsync();

        PlcHeartbeat? received = null;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var subscribeTask = Task.Run(async () =>
        {
            await foreach (var msg in nats.SubscribeAsync<PlcHeartbeat>(subject, serializer: serializer, cancellationToken: cts.Token))
            {
                received = msg.Data;
                break;
            }
        }, cts.Token);

        await Task.Delay(200);
        await nats.PublishAsync(subject, sent, serializer: serializer);

        await subscribeTask;

        // Assert — every field must survive the round trip
        received.Should().NotBeNull();
        received!.DeviceId.Should().Be(deviceId);
        received.Temperature.Should().Be(99.99);
        received.Pressure.Should().Be(750.123);
        received.IsRunning.Should().BeFalse();
        received.Timestamp.Should().Be(timestamp);
    }

    /// <summary>
    /// Full pipeline: publish heartbeats over real NATS, feed received messages
    /// into DeviceTracker, and verify UP/DOWN status detection works end-to-end.
    /// </summary>
    [Fact]
    public async Task DeviceTracker_WithRealNatsMessages()
    {
        // Arrange
        var testId = Guid.NewGuid().ToString("N")[..8];
        var aliveDeviceId = $"PIPE-{testId}-ALIVE";
        var silentDeviceId = $"PIPE-{testId}-SILENT";
        var serializer = NatsJsonSerializer<PlcHeartbeat>.Default;
        var expectedIds = new HashSet<string> { aliveDeviceId, silentDeviceId };
        var tracker = new DeviceTracker(TimeSpan.FromSeconds(5));

        await using var nats = new NatsConnection(new NatsOpts { Url = NatsUrl });
        await nats.ConnectAsync();

        var now = DateTimeOffset.UtcNow;

        // Publish heartbeats — alive device is recent, silent device is old
        var aliveHeartbeat = new PlcHeartbeat
        {
            DeviceId = aliveDeviceId,
            Temperature = 55.0,
            Pressure = 900.0,
            IsRunning = true,
            Timestamp = now
        };

        var silentHeartbeat = new PlcHeartbeat
        {
            DeviceId = silentDeviceId,
            Temperature = 40.0,
            Pressure = 800.0,
            IsRunning = false,
            Timestamp = now.AddSeconds(-10) // 10 seconds old — exceeds 5s timeout
        };

        // Collect messages via subscription
        var receivedMessages = new List<PlcHeartbeat>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var subscribeTask = Task.Run(async () =>
        {
            await foreach (var msg in nats.SubscribeAsync<PlcHeartbeat>(NatsSubjects.AllHeartbeats, serializer: serializer, cancellationToken: cts.Token))
            {
                if (msg.Data is not null && expectedIds.Contains(msg.Data.DeviceId))
                    receivedMessages.Add(msg.Data);

                if (receivedMessages.Count >= 2)
                    break;
            }
        }, cts.Token);

        await Task.Delay(200);
        await nats.PublishAsync(NatsSubjects.ForDevice(aliveDeviceId), aliveHeartbeat, serializer: serializer);
        await nats.PublishAsync(NatsSubjects.ForDevice(silentDeviceId), silentHeartbeat, serializer: serializer);

        await subscribeTask;

        // Act — feed received messages into DeviceTracker
        foreach (var hb in receivedMessages)
            tracker.RecordHeartbeat(hb.DeviceId, hb.Timestamp);

        // Assert — check statuses as of "now"
        var aliveStatus = tracker.GetStatus(aliveDeviceId, asOf: now);
        aliveStatus.IsUp.Should().BeTrue("alive device heartbeat is recent");

        var silentStatus = tracker.GetStatus(silentDeviceId, asOf: now);
        silentStatus.IsUp.Should().BeFalse("silent device heartbeat is 10s old, exceeding 5s timeout");
    }
}
