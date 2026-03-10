namespace NatsPoc.Shared;

/// <summary>
/// Central registry of NATS subject strings used across the solution.
///
/// NATS uses a subject-based messaging model:
/// - Subjects are dot-separated strings (like "plc.PLC-PRESS-001.heartbeat")
/// - Wildcards: '*' matches a single token, '>' matches one or more tokens
/// - Publishers send to a specific subject; subscribers can use wildcards to
///   receive from multiple subjects at once.
///
/// Pattern: plc.{deviceId}.heartbeat
/// </summary>
public static class NatsSubjects
{
    /// <summary>
    /// Builds the specific subject for a given device.
    /// Example: ForDevice("PLC-PRESS-001") → "plc.PLC-PRESS-001.heartbeat"
    /// </summary>
    public static string ForDevice(string deviceId) => $"plc.{deviceId}.heartbeat";

    /// <summary>
    /// Wildcard subject to subscribe to ALL device heartbeats.
    /// The '*' wildcard matches any single token, so this receives
    /// heartbeats from every device.
    /// </summary>
    public const string AllHeartbeats = "plc.*.heartbeat";
}
