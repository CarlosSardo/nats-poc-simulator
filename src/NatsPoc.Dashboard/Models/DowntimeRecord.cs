using System.ComponentModel.DataAnnotations;

namespace NatsPoc.Dashboard.Models;

public class DowntimeRecord
{
    public int Id { get; set; }

    [Required]
    public string DeviceId { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    public double? DurationSeconds { get; set; }

    public string Reason { get; set; } = string.Empty;

    public bool IsResolved { get; set; }
}
