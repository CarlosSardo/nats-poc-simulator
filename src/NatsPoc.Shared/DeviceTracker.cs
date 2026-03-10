using System.Collections.Concurrent;

namespace NatsPoc.Shared;

/// <summary>
/// Tracks PLC device heartbeats and determines if devices are up or down.
/// A device is considered DOWN if no heartbeat has been received within the configured timeout.
/// 
/// Dallas: implement the body of each method. The test suite in NatsPoc.Tests defines the contract.
/// </summary>
public class DeviceTracker
{
    private readonly TimeSpan _downTimeout;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSeen = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset?> _downSince = new();

    public DeviceTracker(TimeSpan downTimeout)
    {
        _downTimeout = downTimeout;
    }

    /// <summary>
    /// Record that a heartbeat was received from a device at the given timestamp.
    /// If the device was previously down, it should now be marked as up.
    /// </summary>
    public void RecordHeartbeat(string deviceId, DateTimeOffset timestamp)
    {
        _lastSeen[deviceId] = timestamp;
        _downSince[deviceId] = null; // Device is alive — clear any down marker
    }

    /// <summary>
    /// Get the current status of a specific device, evaluated at the given point in time.
    /// If asOf is not provided, uses DateTimeOffset.UtcNow.
    /// </summary>
    public DeviceStatus GetStatus(string deviceId, DateTimeOffset? asOf = null)
    {
        var now = asOf ?? DateTimeOffset.UtcNow;

        if (!_lastSeen.TryGetValue(deviceId, out var lastSeen))
            throw new KeyNotFoundException($"Device '{deviceId}' is not being tracked.");

        var elapsed = now - lastSeen;
        var isUp = elapsed < _downTimeout;

        DateTimeOffset? downSince = null;
        if (!isUp)
        {
            // If we already recorded a downSince, keep it; otherwise set it now
            if (_downSince.TryGetValue(deviceId, out var existing) && existing.HasValue)
                downSince = existing;
            else
            {
                downSince = lastSeen + _downTimeout;
                _downSince[deviceId] = downSince;
            }
        }
        else
        {
            _downSince[deviceId] = null;
        }

        return new DeviceStatus(isUp, lastSeen, downSince);
    }

    /// <summary>
    /// Get the statuses of all tracked devices, evaluated at the given point in time.
    /// </summary>
    public IReadOnlyDictionary<string, DeviceStatus> GetAllStatuses(DateTimeOffset? asOf = null)
    {
        var result = new Dictionary<string, DeviceStatus>();
        foreach (var deviceId in _lastSeen.Keys)
        {
            result[deviceId] = GetStatus(deviceId, asOf);
        }
        return result;
    }
}
