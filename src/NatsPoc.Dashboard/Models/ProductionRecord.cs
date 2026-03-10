using System.ComponentModel.DataAnnotations;

namespace NatsPoc.Dashboard.Models;

public class ProductionRecord
{
    public int Id { get; set; }

    [Required]
    public string DeviceId { get; set; } = "";

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    public int PartsProduced { get; set; }

    public int RejectCount { get; set; }

    public int GoodCount => PartsProduced - RejectCount;
}
