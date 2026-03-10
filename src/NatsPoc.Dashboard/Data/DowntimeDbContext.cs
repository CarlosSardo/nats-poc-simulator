using Microsoft.EntityFrameworkCore;
using NatsPoc.Dashboard.Models;

namespace NatsPoc.Dashboard.Data;

public class DowntimeDbContext : DbContext
{
    public DowntimeDbContext(DbContextOptions<DowntimeDbContext> options) : base(options) { }

    public DbSet<DowntimeRecord> DowntimeRecords => Set<DowntimeRecord>();

    public DbSet<ProductionRecord> ProductionRecords => Set<ProductionRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DowntimeRecord>(entity =>
        {
            entity.HasIndex(e => e.DeviceId);
            entity.HasIndex(e => e.StartedAt);
            entity.HasIndex(e => new { e.DeviceId, e.IsResolved });
        });

        modelBuilder.Entity<ProductionRecord>(entity =>
        {
            entity.HasIndex(e => e.DeviceId);
            entity.HasIndex(e => e.Timestamp);
        });
    }
}
