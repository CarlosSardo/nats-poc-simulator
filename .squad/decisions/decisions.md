# Decisions Log

---

## 2026-03-09: Architecture — NATS Downtime Detector

**By:** Ripley (Lead) | **Status:** Decided

### Solution Structure
Three projects under `src/`, one `.sln` at repo root:
- **NatsPoc.Shared** — Class library. Owns the `PlcHeartbeat` message record and `NatsSubjects` constants.
- **NatsPoc.PlcSimulator** — Console app (Generic Host). Simulates 5 PLC devices publishing heartbeats.
- **NatsPoc.DowntimeDetector** — Worker Service. Subscribes via wildcard, tracks last-seen, logs UP/DOWN transitions.

### Key Decisions
1. **NATS subject pattern:** `plc.{deviceId}.heartbeat` — wildcard `plc.*.heartbeat` for detector.
2. **NATS.Net v2+** with `NatsJsonSerializer<T>` for System.Text.Json serialization.
3. **.NET 8, Generic Host** — DI, config, logging for free.
4. **Docker-compose for NATS only** — .NET apps run locally via `dotnet run`.
5. **ConcurrentDictionary for state** — in-memory, no database.
6. **Configurable thresholds** via `appsettings.json`.
7. **PlcHeartbeat is a record** — immutable, `required` properties.
8. **No JetStream** — plain pub/sub sufficient for learning.

---

## 2026-03-09: Lambert test plan — DeviceTracker contract

**By:** Lambert (Tester)

Created `tests/NatsPoc.Tests/` with 9 xUnit tests defining the contract for `DeviceTracker`.

### Key test design decisions
1. Configurable `TimeSpan downTimeout` — not hardcoded.
2. `GetStatus()` / `GetAllStatuses()` accept optional `asOf` for deterministic testing.
3. Unknown device throws `KeyNotFoundException`.
4. Timeout boundary is exclusive (`<` not `<=`): at exactly `elapsed == timeout`, device is DOWN.
5. Recovery is immediate: one heartbeat after downtime → UP, `DownSince` cleared.

---

## 2026-03-09: Dallas — Full implementation of NATS PoC services

**By:** Dallas (Backend Dev)

### Decisions made
1. **NATS subject pattern corrected:** `plc.{deviceId}.heartbeat` (wildcard: `plc.*.heartbeat`).
2. **Kept Ripley's hosted service pattern** (`BackgroundService` / `IHost`).
3. **Used DeviceTracker from Shared** — single source of domain logic.
4. **Added `Pressure` field to PlcHeartbeat** (was missing from Models version).
5. **Removed duplicate PlcHeartbeat** — canonical location is `Models/PlcHeartbeat.cs`.
6. **Serializer namespace:** `NATS.Client.Serializers.Json` (not `NATS.Client.Core`).
7. **Downtime threshold:** 15 seconds per architecture spec.
8. **Per-device Tasks** with randomized 2-5s intervals, ~10% offline probability.

---

## 2026-03-09: Ash — Full Re-Review Verdict

**By:** Ash (SME & Reviewer) | **Verdict: ✅ APPROVED**

All six original review items resolved:
1. Integration tests — ✅ 4 integration tests added.
2. Structured logging — ✅ All `Console.Write*` replaced with `ILogger`.
3. Deserialization try/catch — ✅ Inner try/catch with warning-level logging.
4. Empty DeviceState.cs deleted — ✅ Confirmed removed.
5. Subscription disposal — ⏭️ Accepted as-is for POC (`await using` + cancellation tokens).
6. Random.Shared — ✅ All `new Random()` replaced.

Codebase rated solid for POC: 0 warnings, consistent NATS subjects, explicit serialization, boundary-tested timeout logic, thread safety verified.

---

## 2026-03-10: Dashboard Architecture — SignalR + NATS Backend

**By:** Ripley (Lead) / Dallas (Backend Dev) | **Status:** Implemented

### Architecture
Added `NatsPoc.Dashboard` — ASP.NET Core 8 web app with SignalR hub at `/hubs/dashboard`.
Vanilla HTML/CSS/JS frontend (no SPA framework). Background NATS worker pushes live updates; hub sends initial state on connect.

### Key Decisions
1. **SignalR CORS:** `SetIsOriginAllowed(_ => true)` + `AllowCredentials()` (WebSockets requires credentials; `AllowAnyOrigin` conflicts). Dev-only — lock down for production.
2. **ISO 8601 timestamps** (`"o"` format) in all SignalR payloads for consistent JS parsing.
3. **Singleton DeviceTracker** shared between SignalR hub and NATS background worker.
4. **Cancellation token** passed to `SendAsync` for clean shutdown.

---

## 2026-03-10: SQLite for Downtime History

**By:** Dallas (Backend Dev) | **Status:** Implemented

### Context
Persistent downtime history with reason and duration for all PLC devices.

### Key Decisions
1. **EF Core + SQLite** (`downtime.db` in content root). `EnsureCreated()` at startup — no migrations.
2. **`IServiceScopeFactory`** pattern for DB access from singleton BackgroundService.
3. **New SignalR events:** `ReceiveDowntimeStarted`, `ReceiveDowntimeResolved`, `ReceiveDowntimeHistory` (last 50 on connect).
4. **REST endpoint:** `GET /api/downtimes?deviceId=` — last 100 records, filterable.
5. **`OrderByDescending(r => r.Id)`** instead of DateTimeOffset columns — SQLite cannot ORDER BY DateTimeOffset (bug found by Lambert).

---

## 2026-03-10: Downtime History UI — Client-Side Architecture

**By:** Parker (Frontend Dev) | **Status:** Implemented

### Key Decisions
1. **Client-side filtering:** Downtime records stored in JS array; filter tabs re-render from memory — no API calls.
2. **In-place row patching:** `ReceiveDowntimeResolved` finds `<tr>` by `data-dt-id` and updates cells directly.
3. **Shared 1-second ticker:** Active downtime durations use the same `setInterval` as device cards.
4. **Max 50 visible rows:** Matches event log cap. Oldest trimmed from DOM but kept in array for stats.
5. **XSS-safe rendering:** `escapeHtml()` via DOM text node pattern.

---

## 2026-03-10: Lambert — Downtime History Tests

**By:** Lambert (Tester) | **Status:** Implemented

### Key Decisions
1. **In-memory SQLite** via `SqliteConnection("DataSource=:memory:")` — fast, isolated per test class.
2. **Real ServiceCollection** for `IServiceScopeFactory` — mocked approach failed with `GetRequiredService<T>()`.
3. **30-second tracker timeout** with 60-second-old heartbeats for service tests — prevents recovery heartbeat re-timeout.

### Bug Found & Fixed
SQLite DateTimeOffset ORDER BY throws `NotSupportedException`. Fixed in `DeviceStatusMonitorService.cs` and `DashboardHub.cs` to use `OrderByDescending(r => r.Id)`.
