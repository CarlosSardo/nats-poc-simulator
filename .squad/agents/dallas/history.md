# Dallas — History

## Project Context
- **Project:** nats-poc — A NATS-based downtime detector for simulated PLC devices
- **Stack:** .NET / C#, NATS messaging
- **User:** Carlos Sardo
- **Goal:** Upskilling project to learn NATS. Simulate PLC device data streams, detect device up/down status.

## Learnings
- **Dashboard backend (2026-03-10):** Created `NatsPoc.Dashboard` ASP.NET Core 8 project with SignalR + NATS.
  - Key files: `src/NatsPoc.Dashboard/Program.cs`, `Hubs/DashboardHub.cs`, `Services/NatsHeartbeatService.cs`
  - Pattern: Shared singleton `DeviceTracker` injected into both the SignalR hub (for initial state on connect) and the background NATS worker (for live updates).
  - NATS subscription uses `NatsJsonSerializer<PlcHeartbeat>.Default` — same pattern as DowntimeDetector.
  - SignalR CORS configured with `SetIsOriginAllowed(_ => true)` + `AllowCredentials()` for dev mode.
  - Dockerfile uses multi-stage build, copies `NatsPoc.Shared` alongside Dashboard for project reference resolution.
- **SQLite downtime history (2026-03-10):** Added persistent downtime tracking to Dashboard using EF Core + SQLite.
  - New files: `Models/DowntimeRecord.cs`, `Data/DowntimeDbContext.cs`
  - Modified: `Services/DeviceStatusMonitorService.cs`, `Program.cs`, `Hubs/DashboardHub.cs`
  - Pattern: `IServiceScopeFactory` in BackgroundService to create scoped DbContext instances (singleton → scoped boundary).
  - DB file stored at `{ContentRootPath}/downtime.db` — works both locally and in Docker.
  - `EnsureCreated()` called at startup — no migrations needed for this simple schema.
  - Static device name mapping (e.g. PLC-PRESS-001 → "Hydraulic Press") lives in DeviceStatusMonitorService.
  - API endpoint: `GET /api/downtimes?deviceId=...` returns last 100 records.
  - SignalR events added: `ReceiveDowntimeStarted`, `ReceiveDowntimeResolved`, `ReceiveDowntimeHistory`.
  - ⚠️ DashboardHub constructor changed — `DashboardHubTests.cs` needs Lambert to add a `DowntimeDbContext` mock param.

## Cross-Agent Updates (2026-03-10)
- **Lambert** fixed DateTimeOffset ORDER BY bug in `DeviceStatusMonitorService` and `DashboardHub` — SQLite cannot sort DateTimeOffset columns server-side. Use `OrderByDescending(r => r.Id)` instead.
- **Parker** consumed all 3 new SignalR events (`ReceiveDowntimeHistory`, `ReceiveDowntimeStarted`, `ReceiveDowntimeResolved`) — contract matched exactly.
- **Lambert** updated `DashboardHubTests.cs` for the new DashboardHub constructor and dual-message `OnConnectedAsync`.

## Learnings — OEE Backend (2026-07-08)
- **OEE implementation:** Added industry-standard OEE (Availability × Performance × Quality) backend across 7 files.
  - New files: `Models/ProductionRecord.cs`, `Models/OeeSnapshot.cs`, `Services/OeeCalculationService.cs`
  - Modified: `PlcHeartbeat.cs` (+PartsProduced, +RejectCount), `PlcSimulatorWorker.cs` (production simulation), `NatsHeartbeatService.cs` (+IServiceScopeFactory, stores ProductionRecords), `DashboardHub.cs` (sends OEE on connect), `Program.cs` (OeeCalculationService registration + 2 API endpoints), `DowntimeDbContext.cs` (+ProductionRecords DbSet with indexes)
  - Pattern: `OeeCalculationService` is a BackgroundService with 10s PeriodicTimer, pushes `ReceiveOeeUpdate` via SignalR. Static `IdealPartsPerSecond` dict + `AllDeviceIds` array shared with API endpoints in Program.cs.
  - Pattern: Simulator uses ±20% production variation via `0.8 + (Random.Shared.NextDouble() * 0.4)` and per-part reject roll against configurable `RejectRate`.
  - ⚠️ DowntimeRecord uses `EndedAt` (not `ResolvedAt`) — caught during build.
  - ⚠️ `CalculatePlantOee()` returns `double`, not `OeeSnapshot` — API endpoint wraps it into an OeeSnapshot with aggregated totals.
  - API endpoints: `GET /api/oee` (all devices + plant aggregate), `GET /api/oee/{deviceId}` (single device).
  - NatsHeartbeatService now takes 5 constructor params (added `IServiceScopeFactory`). No direct test impact since it's a BackgroundService.
  - DashboardHub `OnConnectedAsync` now sends 3 messages (was 2): `ReceiveAllStatuses`, `ReceiveDowntimeHistory`, `ReceiveOeeUpdate`. Existing tests still pass because they assert on specific method names, not total call count.
  - All 29 tests pass (was 25 before — count includes new test infrastructure).

## Learnings — Docker: DowntimeDetector (2026-03-10)
- **Dockerized DowntimeDetector:** Created `src/NatsPoc.DowntimeDetector/Dockerfile` following the same multi-stage pattern as PlcSimulator (worker service → `dotnet/runtime:8.0` base, not aspnet).
- **docker-compose.yml:** Added `detector` service with `Nats__Url=nats://nats:4222`, depends on `nats`. Updated `dashboard` to also depend on `detector`.
- **`.dockerignore`:** Extended with `*.md`, `.gitignore`, `.gitattributes`, `*.sln` excludes to slim build context.
- Key files: `src/NatsPoc.DowntimeDetector/Dockerfile`, `docker-compose.yml`, `.dockerignore`.
