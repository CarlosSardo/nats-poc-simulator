namespace NatsPoc.Shared.Models;

/// <summary>
/// Message published by each PLC device on every heartbeat interval.
/// This is the payload that travels over NATS — serialized as JSON.
/// </summary>
public sealed record PlcHeartbeat
{
    public required string DeviceId { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public double Temperature { get; init; }
    public double Pressure { get; init; }
    public bool IsRunning { get; init; }
    public int PartsProduced { get; init; }
    public int RejectCount { get; init; }
}
