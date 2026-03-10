namespace NatsPoc.Shared;

/// <summary>
/// Represents the current status of a tracked PLC device.
/// IsUp: true if the device has sent a heartbeat within the timeout window.
/// LastSeen: the timestamp of the most recent heartbeat.
/// DownSince: when the device was first detected as down (null if currently up).
/// </summary>
public record DeviceStatus(bool IsUp, DateTimeOffset LastSeen, DateTimeOffset? DownSince);
