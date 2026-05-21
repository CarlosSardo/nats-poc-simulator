# Squad Decisions

## Active Decisions

### 2026-05-21: SPEC-DEMO-02 authored — Expanded Fleet, Visual Identity & UX Polish
**By:** Ripley (Lead) — at Carlos Sardo's request
**What:** Authored `specs/spec-demo-02.md` defining four work items:
  1. **Task 1 — Two new simulated machines** (Owner: Dallas, Reviewer: Ash). Add `PLC-CNC-006` (CNC Mill, `ThermalDrift` profile) and `PLC-PAINT-007` (Paint Booth, `BurstReject` profile) to `PlcSimulatorWorker.cs`, plus matching cards/gauges in `index.html`.
  2. **Task 2 — Per-device emoji identity** (Owner: Parker, Reviewer: Ripley). New `wwwroot/js/device-icons.js` with `getDeviceEmoji(id)` helper, applied to device cards and OEE gauge labels with type-prefix fallback for unknown devices.
  3. **Task 3 — Fuzzy search on Event Log** (Owner: Parker, Reviewer: Ripley). Search input above `#event-log`, client-side substring + subsequence match, no new dependencies.
  4. **Task 4 — Architecture diagram → single Mermaid** (Owner: Ripley, Reviewer: Ash). Remove ASCII block from README, consolidate into one Mermaid flowchart reflecting 7 PLCs.
**Sequencing:** Task 1 first → unblocks Task 2. Tasks 2 & 3 parallel after Task 1. Task 4 independent.
**Why:** Grow the demo fleet to 7 machines, distinguish them visually, make the live log searchable, and replace dual-format architecture docs with a single Mermaid source-of-truth. All work uses existing patterns (NATS wildcard subscribe, vanilla JS, xUnit) — no new infra.
**Out of scope:** auth, NATS subject changes, EF schema migration, new test infra (no Playwright), build pipeline.

### 2026-05-21: SPEC-DEMO-02 Task 1 — Two new simulated machines (PLC-CNC-006, PLC-PAINT-007)
**By:** Dallas (Backend Dev) — at Carlos Sardo's request
**What:** Added `PLC-CNC-006` (CNC Mill, `ThermalDrift`) and `PLC-PAINT-007` (Paint Booth, `BurstReject`) to `PlcSimulatorWorker.Devices`; extended `FailureProfile` enum with `ThermalDrift` and `BurstReject`; updated `index.html` (cards + per-device OEE gauges + `stat-total` 5→7) and `dashboard.js` `DEVICE_NAMES` map. `PlcHeartbeat` schema unchanged — drift/burst surface only via existing `Temperature`/`RejectCount` fields.
**Drift/burst state:** Held as method-locals (`driftTempOffset`, `driftRejectBoost`, `burstUntil`) inside `SimulateDeviceAsync` rather than a shared per-device dictionary. Each device task is naturally isolated by `Devices.Select(SimulateDeviceAsync)`, so locals avoid shared-state races. Forced drift reset on outage recovery is inline in the recovery branch.
**Future:** If profile count grows past ~4, extract `IFailureProfileState` strategy to keep `SimulateDeviceAsync` readable.
**Build:** `dotnet build nats-poc.sln` green.

### 2026-05-21: SPEC-DEMO-02 Task 2 — Per-device emoji identity
**By:** Parker (Frontend Dev) — at Carlos Sardo's request
**What:** New global script `wwwroot/js/device-icons.js` exposes `window.getDeviceEmoji(id)`, `window.DEVICE_EMOJI`, `window.DEVICE_TYPE_EMOJI` via plain IIFE — no module system, no build step. Uses `\uXXXX` escapes so the file is ASCII-safe.
**Load order:** `signalr` (CDN) → `js/device-icons.js` → `js/dashboard.js`. `device-icons.js` MUST come before `dashboard.js` (which calls `window.getDeviceEmoji` at DOMContentLoaded).
**Render-time prefix (NOT hardcoded):** Static cards in `index.html` ship with plain names; `applyDeviceEmojis()` runs once at DOMContentLoaded and rewrites every `.device-card[data-device-id] .device-name` and every `.oee-gauge-container[id^="device-oee-"] .oee-device-label` to `emoji + U+00A0 + originalName`. Original cached in `dataset.rawName` / `dataset.rawLabel` → idempotent re-application.
**Lookup order in `getDeviceEmoji(id)`:** (1) exact ID match in `DEVICE_EMOJI`; (2) regex `/^PLC-([A-Z]+)-/` middle-token fallback against `DEVICE_TYPE_EMOJI` (so future `PLC-PRESS-008` still gets 🔨); (3) generic factory fallback `🏭`.
**Build:** `dotnet build` green.

### 2026-05-21: SPEC-DEMO-02 Task 3 — Fuzzy search on Event Log (with `<mark>` highlight stretch)
**By:** Parker (Frontend Dev) — at Carlos Sardo's request
**What:** Implemented Event Log fuzzy search (substring OR subsequence, case-insensitive) per spec. Filtering uses a `.hidden` CSS class (`display: none`) rather than inline `style.display` — DOM stays intact and `MAX_LOG_ENTRIES` retention is unaffected. Active query persists in module-level `activeLogQuery`; `addLogEntry` consults it before insertion. Escape inside `#event-log-search` clears input and restores all entries.
**Stretch shipped:** `<mark>` highlight on substring matches (subsequence skipped to avoid noise per spec). Each entry caches un-highlighted text in `dataset.rawText` so re-highlighting on every keystroke is idempotent and never stacks `<mark>` tags.
**Why `.hidden` over inline style:** Cheap, themable, consistent with the existing `.downtime-info.hidden` pattern in the codebase.
**Files:** `wwwroot/index.html`, `wwwroot/css/dashboard.css`, `wwwroot/js/dashboard.js`. No backend changes.

### 2026-05-21: SPEC-DEMO-02 Task 4 — Architecture diagram consolidated to single Mermaid source
**By:** Ripley (Lead) — at Carlos Sardo's request
**What:** Removed ASCII art block under `## 🏗️ Architecture` in `README.md` and merged it with the existing Mermaid block into ONE `flowchart TB`. All 7 PLCs listed individually (PRESS, CONV, WELD, PACK, OVEN, CNC, PAINT) rather than collapsed — readable on GitHub's default Mermaid renderer, preserves educational value, matches per-device cards.
**Other choices:** Concrete service names from `src/NatsPoc.Dashboard/Services/` and `src/NatsPoc.DowntimeDetector/` (`NatsHeartbeatService`, `DeviceTracker`, `OeeCalculationService`, `DashboardHub`, `DowntimeDbContext`, `DowntimeDetectorWorker`) so the diagram doubles as a code map. `<br/>` line breaks (portable across renderers). `&amp;` escape in `record downtime &amp; production` edge label. Renamed NATS subgraph ID `NATS` → `NATSServer` to avoid keyword collision.
**Future:** If fleet grows past ~10 devices, switch to collapsed "N PLCs" form per spec escape hatch.

### 2026-05-21: SPEC-DEMO-02 Task 1 test coverage + ThermalDrift/BurstReject seam gap flagged
**By:** Lambert (Tester) — at Carlos Sardo's request
**Shipped:** 8 new test cases in `tests/NatsPoc.Tests/DeviceTrackerTests.cs` — `[Theory]` parametrized across all 7 device IDs validating up→down→up lifecycle through public `RecordHeartbeat`/`GetStatus` surface, plus `[Fact]` asserting `GetAllStatuses` returns all 7. Totals: **57 total / 53 unit (all pass) / 4 NATS integration (filtered)**. No regressions. Built green against existing xUnit + FluentAssertions — no new packages, no production touch.
**Gap flagged for Ash + Dallas:** `ThermalDrift`/`BurstReject` `EvaluateFailure` math is currently NOT unit-testable from `NatsPoc.Tests`. Blockers: `EvaluateFailure` is `private static`, `FailureProfile` enum is `internal`, `Devices` array and `PlcDeviceConfig` are private, drift/burst state is method-local, `Random.Shared` is captured directly (no injection seam), and `NatsPoc.Tests.csproj` doesn't reference `NatsPoc.PlcSimulator`.
**Recommended seam options (least → most invasive):**
1. `InternalsVisibleTo("NatsPoc.Tests")` + `<ProjectReference>` + promote `EvaluateFailure` to `internal static`, `Devices`/`PlcDeviceConfig` to `internal`. Lowest risk — ~10 lines.
2. Inject `Random` as a parameter (defaulting to `Random.Shared`) → seeded probabilistic tests possible.
3. Extract drift/burst state into `NatsPoc.Shared` records with pure `Step(roll)` methods. Largest refactor.
**Status:** Lambert parked on this surface until Ash/Dallas decide on a seam. Per Lambert's charter, no production code was touched.

### 2026-05-21: SPEC-DEMO-02 review verdicts — Task 1 ✅ + Task 4 ✅ APPROVED
**By:** Ash (SME & Reviewer) — at Carlos Sardo's request
**Task 1 (Dallas):** APPROVED. All acceptance criteria verified — `Devices` ranges match spec exactly, `FailureProfile` enum extended correctly, `ThermalDrift` drift state caps (`+20°C`, `+0.10` reject) and ~0.5%/cycle reset + forced reset on outage recovery confirmed, `BurstReject` 6% entry / 10–20s window / `[0.25, 0.35]` rate confirmed, cascade group untouched, `index.html` cards + gauges + `stat-total: 7` correct, `dashboard.js` `DEVICE_NAMES` extended, `PlcHeartbeat` schema unchanged, build green.
**Task 4 (Ripley):** APPROVED. ASCII removed, single `flowchart TB`, all 7 PLCs listed, `plc.*.heartbeat` wildcard fan-out with both subscribers visible, four subgraphs, meaningful edge labels, concrete service names, clean Mermaid syntax (`&amp;` escape, `<br/>` breaks, `NATSServer` ID rename).
**Non-blocking follow-ups:**
- Features section in README still reads "5 simulated PLCs" — out of scope for Task 4 but stale after Task 1. Schedule a tiny doc-only refresh to "7" + add CNC Mill / Paint Booth to Simulated Devices section.
- Drift state lives in two separate locals (`driftTempOffset` + `driftRejectBoost`) with reset branch repeated twice — agree with Dallas's own note to extract `IFailureProfileState` once a 5th drift-flavored profile lands.
- Tiny code comment explaining why `🧊` reset log only fires when `driftTempOffset > 0.1` (avoids noise on fresh startup).
**Lockout:** Both APPROVED → reviewer-rejection lockout does NOT apply.

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
