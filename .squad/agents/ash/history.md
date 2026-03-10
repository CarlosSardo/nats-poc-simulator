# Ash — History

## Project Context
- **Project:** nats-poc — NATS-based downtime detector for simulated PLC devices
- **Stack:** .NET 8 / C#, NATS.Net v2+ (NATS.Client.Core), Docker, xUnit
- **User:** Carlos Sardo
- **Purpose:** Upskilling project to learn NATS messaging patterns

## Architecture (inherited)
- Subject pattern: `plc.{deviceId}.heartbeat` with wildcard `plc.*.heartbeat`
- 5 simulated PLCs: PLC-PRESS-001, PLC-WELD-003, PLC-CONV-002, PLC-PACK-004, PLC-OVEN-005
- DeviceTracker with configurable 15s timeout for downtime detection
- NatsJsonSerializer<T> for serialization via System.Text.Json
- Docker Compose with nats:2-alpine on ports 4222 + 8222

## Key Files
- `src/NatsPoc.Shared/` — PlcHeartbeat record, NatsSubjects, DeviceTracker, DeviceStatus
- `src/NatsPoc.PlcSimulator/` — BackgroundService simulating PLC heartbeats
- `src/NatsPoc.DowntimeDetector/` — Worker service with wildcard subscription
- `tests/NatsPoc.Tests/` — 9 xUnit tests for DeviceTracker contract

## Learnings

## Cross-Agent Updates (2026-03-10)
- **Dallas** added SQLite downtime persistence to Dashboard. New files: `Models/DowntimeRecord.cs`, `Data/DowntimeDbContext.cs`. Uses `IServiceScopeFactory` pattern.
- **Lambert** found SQLite DateTimeOffset ORDER BY bug — relevant for future reviews. SQLite cannot sort DateTimeOffset columns; use integer surrogate (Id) instead.
- Test count is now 25 (16 original + 9 downtime). All pass.
