namespace NatsPoc.Dashboard.Models;

public class OeeSnapshot
{
    public string DeviceId { get; set; } = "";
    public double Availability { get; set; }
    public double Performance { get; set; }
    public double Quality { get; set; }
    public double Oee => Availability * Performance * Quality;
    public int TotalPartsProduced { get; set; }
    public int TotalRejects { get; set; }
    public int GoodParts => TotalPartsProduced - TotalRejects;
    public double PlannedTimeSeconds { get; set; }
    public double RunTimeSeconds { get; set; }
    public double DowntimeSeconds { get; set; }
    public DateTimeOffset CalculatedAt { get; set; } = DateTimeOffset.UtcNow;
}
