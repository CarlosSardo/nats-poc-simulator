# Spec Demo 02 — Expanded Fleet, Visual Identity & UX Polish

| Field | Value |
|---|---|
| **Spec ID** | `SPEC-DEMO-02` |
| **Status** | Proposed |
| **Author** | Ripley (Lead) |
| **Date** | 2026-05-21 |
| **Related Decisions** | [`.squad/decisions.md`](../.squad/decisions.md) |

---

## 1. Overview / Goals

The PoC has been stable on five simulated PLCs since the initial dashboard work (see Ripley's history, 2026-03-10). It is time to grow the fleet and tighten the operator experience. This spec adds two more simulated machines with distinct failure profiles, gives every device a unique emoji identity in the UI, makes the Event Log searchable so operators can quickly isolate one machine's chatter, and replaces the ASCII architecture diagram in the README with a single Mermaid diagram that survives in source control reviews.

The work is intentionally small in scope per task and chosen to exercise the existing patterns (NATS wildcard subscribe, dashboard auto-discovery, vanilla JS UI, SQLite persistence) rather than introduce new infrastructure. After this spec lands, a contributor should see seven devices flowing end-to-end, distinguish them at a glance, search the live log, and understand the system from one diagram.

## 2. Out of Scope

- No authentication / authorization changes.
- No new NATS subjects — the existing `plc.{id}.heartbeat` pattern with the `plc.*.heartbeat` wildcard already supports new devices automatically.
- No EF Core / SQLite schema migration. New devices flow into existing `DowntimeRecord` and `OeeSnapshot` tables unchanged.
- No new test infrastructure (no Playwright, no headless browser harness — manual smoke for UI).
- No npm / build pipeline. The dashboard ships static files from `wwwroot` and that stays the same.
- No backend filtering for the Event Log search (Task 3 is purely client-side).

---

## 3. Tasks

### Task 1 — Two new simulated machines

- **Owner:** Dallas
- **Reviewer:** Ash
- **Depends on:** none

#### Description
Extend `PlcSimulatorWorker` to publish heartbeats for two additional PLCs that exercise failure profiles **not already in use** by the existing five (`Frequent`, `Flicker`, `LongOutage`, `Cascade`). The dashboard already subscribes via `plc.*.heartbeat` and auto-discovers devices, so no Dashboard wiring change should be strictly necessary — but the two new device IDs must be added to the static device grid in `index.html` and to the per-device OEE gauges so they render alongside the existing five.

#### Proposed devices

| Device ID | Name | New profile | Behavior |
|---|---|---|---|
| `PLC-CNC-006` | CNC Mill | `ThermalDrift` | Rarely fails outright (~3% / cycle, 8–15s outage). Instead, between resets, **temperature drifts upward** and **reject rate climbs proportionally**. A "reset" (rare scheduled blip) returns both to baseline. Demonstrates gradual quality degradation. |
| `PLC-PAINT-007` | Paint Booth | `BurstReject` | Stays online with normal reject rate (~2%) for long stretches, then enters a **reject burst window** (~6% chance / cycle to enter a 10–20s window) where reject rate spikes to 25–35%. No downtime, but OEE Quality should visibly dip. |

#### Synthetic payload ranges

| Device | MinTemp | MaxTemp | MinPressure | MaxPressure | IdealPartsPerCycle | Baseline RejectRate |
|---|---|---|---|---|---|---|
| `PLC-CNC-006` (CNC Mill) | 35 | 75 (drifts up to ~95 before reset) | 80 | 180 | 6 | 0.02 (drifts up to ~0.12) |
| `PLC-PAINT-007` (Paint Booth) | 22 | 38 | 5 | 25 | 15 | 0.02 (spikes to 0.25–0.35 in burst) |

#### Acceptance Criteria
- [ ] Two new entries in the `Devices` array in `PlcSimulatorWorker.cs` with the IDs, names, and ranges above.
- [ ] `FailureProfile` enum gains two new values: `ThermalDrift` and `BurstReject`. `EvaluateFailure` (or its successor) handles both.
- [ ] For `ThermalDrift`: per-device drift state is tracked across cycles (a small mutable struct or a `ConcurrentDictionary<string, DriftState>`). Temperature published in the heartbeat reflects the drifted value, not just `MinTemp..MaxTemp` random.
- [ ] For `BurstReject`: per-device burst-window state is tracked. While in a burst window, `RejectRate` used for the cycle is sampled from `[0.25, 0.35]`.
- [ ] Cascade group is **not** modified; new devices do not participate in the existing cascade.
- [ ] Dashboard `index.html` includes static `<div class="device-card">` entries for `PLC-CNC-006` and `PLC-PAINT-007`, matching the existing card markup.
- [ ] Dashboard `index.html` includes per-device OEE gauge containers (`#device-oee-PLC-CNC-006`, `#device-oee-PLC-PAINT-007`) matching the existing gauge markup.
- [ ] `stat-total` initial value in `index.html` updated from `5` to `7` (it gets overwritten at runtime, but the static value should match the expected fleet).
- [ ] Both devices appear online within ~5s of `docker compose up`, transition to `OFFLINE` per their profile, and produce `DowntimeRecord` rows when they go down.
- [ ] Both devices flow into the existing OEE pipeline — A/P/Q values render on their gauges and the plant-wide gauge accounts for them.

#### Files to Touch
- `src/NatsPoc.PlcSimulator/PlcSimulatorWorker.cs` — add devices, extend enum, extend `EvaluateFailure`, thread per-device state through `SimulateDeviceAsync`.
- `src/NatsPoc.Dashboard/wwwroot/index.html` — add 2 device cards in the `.device-grid` section, add 2 OEE gauge containers in the per-device OEE section, bump `stat-total` to `7`.
- `src/NatsPoc.Dashboard/wwwroot/js/dashboard.js` — extend the `DEVICE_NAMES` map (search for it; it is the dictionary used for filter labels) so the new IDs render with friendly names.

#### Implementation Notes
- Keep payload conformance: continue publishing the existing `PlcHeartbeat` record from `NatsPoc.Shared.Models`. Do **not** add fields to the heartbeat — drift and burst are simulator-internal concepts that surface only via Temperature and RejectCount.
- For `ThermalDrift` state, the simplest path is a small per-device record passed into `SimulateDeviceAsync`; today the method captures cycle state in locals (`isOffline`, `cycleCount`). Add `currentTempBaseline` and `currentRejectMultiplier` as locals, with a small `MaybeReset()` rule (e.g., 0.5% chance per cycle to fully reset, plus a forced reset after the rare outage).
- For `BurstReject`, track `burstUntil = DateTimeOffset` as a local and compare against `UtcNow`.
- Stagger initial startup is already random (0–2000ms); no change needed.
- Respect existing logging style — emoji prefixes, structured logging with named placeholders.

#### Test Requirements (Lambert)
- Unit test: `EvaluateFailure` (or refactored equivalent) for `ThermalDrift` returns `(false, ...)` on the vast majority of cycles and `(true, 8..15s)` rarely. Use a deterministic seed if needed by extracting `Random` to a parameter.
- Unit test: `BurstReject` window math — given a fixed clock, verify a device enters and exits a burst window correctly.
- Unit test (existing surface): `DeviceTracker` correctly records up/down for the two new device IDs (parametrize existing tests in `DeviceTrackerTests.cs` if straightforward; otherwise add a small focused test).
- No tests required for the static HTML changes.

---

### Task 2 — Distinct emoji per machine in the UI

- **Owner:** Parker
- **Reviewer:** Ripley
- **Depends on:** Task 1 (needs the new device IDs to exist)

#### Description
Every device card and every per-device OEE gauge should display a unique emoji prefix that matches the machine type at a glance. The mapping lives entirely in client-side JavaScript and is keyed on the device ID. Unknown future devices fall back to a generic emoji so auto-discovered PLCs still render.

#### Emoji map (final)

| Device ID | Emoji | Rationale |
|---|---|---|
| `PLC-PRESS-001` | 🔨 | Hydraulic press |
| `PLC-CONV-002` | 🪢 | Conveyor belt |
| `PLC-WELD-003` | ⚡ | Welding (sparks) |
| `PLC-PACK-004` | 📦 | Packaging |
| `PLC-OVEN-005` | 🔥 | Oven heat |
| `PLC-CNC-006` | 🔩 | CNC mill |
| `PLC-PAINT-007` | 🎨 | Paint booth |
| _fallback_ | 🏭 | Generic factory |

#### Acceptance Criteria
- [ ] A new file `src/NatsPoc.Dashboard/wwwroot/js/device-icons.js` exports a `DEVICE_EMOJI` map and a `getDeviceEmoji(deviceId)` helper that returns the mapped emoji or the fallback `🏭`.
- [ ] `index.html` loads `device-icons.js` **before** `dashboard.js` (plain `<script src="js/device-icons.js"></script>` — no module system).
- [ ] `dashboard.js` prefixes every device card heading (`.device-name`) and every OEE gauge label (`.oee-device-label`) with the emoji + a non-breaking space, e.g. `"🔨\u00A0Hydraulic Press"`.
- [ ] The prefix is applied at render time (not hard-coded into `index.html`) so auto-discovered devices also get prefixed.
- [ ] Lookup is **exact match by full device ID first**, with a secondary match on the middle token (e.g., `PRESS` in `PLC-PRESS-001`) so a future `PLC-PRESS-008` would still get 🔨 without a code change.
- [ ] Visual smoke: every card and gauge in the running dashboard shows the correct emoji for all seven devices.

#### Files to Touch
- `src/NatsPoc.Dashboard/wwwroot/js/device-icons.js` — **new file**.
- `src/NatsPoc.Dashboard/wwwroot/index.html` — add the new `<script>` tag before `dashboard.js`.
- `src/NatsPoc.Dashboard/wwwroot/js/dashboard.js` — call `getDeviceEmoji(...)` at the points where card names and OEE labels are populated. Search for `.device-name` and `.oee-device-label` writes (likely in the device-card render and OEE update paths).

#### Implementation Notes
- Vanilla JS only. No build step. No npm. The project ships static files; keep it that way.
- `getDeviceEmoji` is ~10 lines. Pattern:
  ```js
  const DEVICE_EMOJI = { "PLC-PRESS-001": "🔨", /* ... */ };
  const DEVICE_TYPE_EMOJI = { PRESS: "🔨", CONV: "🪢", WELD: "⚡", PACK: "📦", OVEN: "🔥", CNC: "🔩", PAINT: "🎨" };
  function getDeviceEmoji(id) {
    if (DEVICE_EMOJI[id]) return DEVICE_EMOJI[id];
    const m = /^PLC-([A-Z]+)-/.exec(id || "");
    return (m && DEVICE_TYPE_EMOJI[m[1]]) || "🏭";
  }
  ```
- Static cards in `index.html` keep their plain text names; `dashboard.js` overwrites them on first render. Do **not** hard-code emoji in HTML — the renderer is the single source of truth.
- Keep file ordering matter: `device-icons.js` must load before `dashboard.js` (it relies on a global, not a module export).

#### Test Requirements (Lambert)
- Manual smoke test only. UI tests are out of scope per "Out of Scope" section.
- Verification checklist (Parker writes a one-paragraph note in the PR description listing all seven device cards + seven OEE labels with their observed emoji).

---

### Task 3 — Fuzzy search on the Event Log

- **Owner:** Parker
- **Reviewer:** Ripley
- **Depends on:** none (independent of Tasks 1 & 2; ideal candidate to run in parallel with Task 2)

#### Description
Add a search input above the Event Log (`#event-log` in `index.html` line ~301). As the user types, the displayed log entries are filtered client-side. Matching is **case-insensitive substring OR subsequence** so short fuzzy queries work (typing `wld` matches `PLC-WELD-003`). Clearing the input restores the full log. No backend change.

#### Acceptance Criteria
- [ ] A search input appears immediately above the Event Log, inside `.event-log-section`, before the `.event-log` div.
- [ ] Typing in the input filters visible `.event-entry` children of `#event-log` in real time (on `input` event, no debounce needed for ≤200 entries).
- [ ] **Match rule:** an entry matches if the query is a case-insensitive substring of its text **OR** if all characters of the query appear in order (subsequence) in the entry text. Empty query → all entries visible.
- [ ] Filtering hides non-matching entries via `display: none` (or a `hidden` class) — entries are **not removed from the DOM**, so newly arriving entries are filtered against the active query as they're inserted.
- [ ] When a new entry arrives via `addLogEntry(...)` while a query is active, the new entry is filtered before being shown.
- [ ] Clearing the input (or pressing Escape inside the input) restores all entries.
- [ ] **Stretch (optional, mark as done if shipped):** matched substring is wrapped in a `<mark>` tag for visual highlight.
- [ ] Existing entry cap (`MAX_LOG_ENTRIES`) still applies — search does not change retention.
- [ ] Verification scenarios:
  - Type `weld` → only WELD-003 entries visible.
  - Type `001` → only PRESS-001 entries visible.
  - Type `wld` → WELD-003 entries visible (subsequence match).
  - Clear input → all entries visible.

#### Files to Touch
- `src/NatsPoc.Dashboard/wwwroot/index.html` — add `<input type="search" id="event-log-search" placeholder="Filter events…">` inside `.event-log-section`, above `.event-log`.
- `src/NatsPoc.Dashboard/wwwroot/css/dashboard.css` — small style block for `#event-log-search` consistent with the existing dark theme (use existing CSS variables: `--bg-secondary`, `--border-color`, `--text-primary`, `--font-mono`).
- `src/NatsPoc.Dashboard/wwwroot/js/dashboard.js` — wire the input's `input` and `keydown` (Escape) events; modify `addLogEntry` so newly inserted entries respect the active query.

#### Implementation Notes
- Keep the matcher inline — no library, no npm.
  ```js
  function matchesQuery(text, q) {
    if (!q) return true;
    const t = text.toLowerCase();
    const lq = q.toLowerCase();
    if (t.includes(lq)) return true;       // substring
    let i = 0;
    for (const c of t) { if (c === lq[i]) i++; if (i === lq.length) return true; }
    return false;                          // subsequence
  }
  ```
- Apply filter by iterating `#event-log`'s children and toggling a `hidden` class. For the typical log size (≤200 entries) this is fast enough; do not optimize prematurely.
- Persist the active query in a single module-level variable (e.g., `let activeLogQuery = ""`) so `addLogEntry` can consult it.
- Highlighting (stretch): if you ship it, only highlight on substring matches (skip for subsequence — too noisy). Replace the entry's text node only when the entry is rendered, not on every keystroke.

#### Test Requirements (Lambert)
- Manual smoke per the verification scenarios above.
- No xUnit coverage required (this is browser-only logic with no shared C# surface).

---

### Task 4 — Convert Architecture diagram to Mermaid (single source)

- **Owner:** Ripley
- **Reviewer:** Ash
- **Depends on:** none (can land before, alongside, or after Tasks 1-3; if it lands after Task 1, the diagram should reflect 7 PLCs)

#### Description
The README currently has **two** architecture representations stacked: an ASCII art block (≈ lines 28–66 of `README.md`) followed by a Mermaid `flowchart TB` (≈ lines 68–110). Replace the ASCII block and consolidate into a **single, well-labelled Mermaid diagram** that supersedes both. GitHub renders Mermaid natively in markdown; one diagram is easier to maintain and review than two.

#### Acceptance Criteria
- [ ] The ASCII art block under `## 🏗️ Architecture` is removed.
- [ ] A single Mermaid `flowchart` (TB or LR — author's call) replaces both the ASCII and the existing Mermaid block.
- [ ] The diagram clearly shows:
  - All seven PLCs (or "PLC-PRESS-001 … PLC-PAINT-007 (7 devices)" in a single subgraph node if listing seven gets noisy) publishing to NATS.
  - The NATS subject `plc.*.heartbeat` as the wildcard fan-out point.
  - **Two** subscribers on the wildcard: the Dashboard's `NatsHeartbeatService` and the Detector worker.
  - The Dashboard's internal flow: NATS subscriber → `DeviceTracker` → (`OeeCalculationService`, SignalR `DashboardHub`, `DowntimeDbContext`/SQLite) → Browser (via SignalR) and Browser (via REST `/api/oee`, `/api/downtimes`).
- [ ] At least three subgraphs (Simulator, NATS, Dashboard, Detector) for visual grouping.
- [ ] Edge labels are present and meaningful (e.g., `publish JSON heartbeat`, `subscribe plc.*.heartbeat`, `push events`, `HTTP GET`).
- [ ] The diagram renders cleanly on GitHub (no Mermaid syntax errors) — verify by viewing the README on the PR page before requesting review.
- [ ] No other README sections are altered (Features, Quick Start, API Endpoints, etc. unchanged).

#### Files to Touch
- `README.md` — replace the ASCII block + the existing Mermaid block with a single Mermaid block.

#### Implementation Notes
- Concrete service names (preferred over generic labels): `NatsHeartbeatService`, `DeviceTracker`, `OeeCalculationService`, `DashboardHub`, `DowntimeDbContext`, `DowntimeDetectorWorker`. These match the actual files in `src/NatsPoc.Dashboard/Services/` and `src/NatsPoc.DowntimeDetector/`.
- The existing Mermaid block (already rendering on GitHub) is a good starting point — extend it to cover the seven devices and adjust to taste rather than starting over.
- If listing seven devices in the Simulator subgraph is too tall, collapse to a single node labelled `7 PLCs (PRESS, CONV, WELD, PACK, OVEN, CNC, PAINT)` — readability beats completeness here.
- Keep the diagram self-contained: no external links, no images.

#### Test Requirements (Lambert)
- None — this is documentation. Reviewer (Ash) verifies the rendered output on GitHub.

---

## 4. Sequencing / Suggested Order

```
Task 1 (Dallas) ──┬─► Task 2 (Parker)   [needs new device IDs]
                  └─► (also unblocks updated counts in Task 4 if Task 4 lands after)
Task 3 (Parker)   ──► independent — can run in parallel with Task 2
Task 4 (Ripley)   ──► independent — can run anytime
```

- **Task 1 first.** Tasks 2 ships the emoji map keyed on `PLC-CNC-006` / `PLC-PAINT-007`; those IDs need to exist.
- **Tasks 2 and 3 can run in parallel** once Task 1 is in. Both are Parker's, so coordinate the order with Parker — Task 3 is fully decoupled and could even start before Task 1.
- **Task 4 is independent.** If it lands before Task 1, the diagram can already reflect "7 PLCs" since the spec commits to that final shape.

## 5. Testing Strategy

- **Lambert's surface = Task 1 only.** All new C# logic in `PlcSimulatorWorker` for `ThermalDrift` and `BurstReject` profiles needs unit coverage. Existing `DeviceTrackerTests` and `DowntimeHistoryTests` should pass unchanged with the two new device IDs flowing through.
- **No new test infrastructure.** The repo has xUnit only — confirmed no Playwright or browser test harness present. Tasks 2 and 3 are validated by manual smoke (documented checklist in PR description).
- **Regression bar:** after each task, run `dotnet test` from repo root and `docker compose up --build` to confirm dashboard renders all seven devices end-to-end.

## 6. Risks & Open Questions

- **Drift state thread-safety:** `SimulateDeviceAsync` runs one task per device, so per-device state is naturally isolated as method locals — no shared dictionary needed. Confirm during Task 1 review (Ash) that drift state stays local and does not accidentally become shared.
- **Emoji rendering on Windows / older browsers:** modern emoji are reliable on evergreen Chromium / Firefox. If the demo target ever drifts to a constrained browser (kiosk, embedded), we may need to fall back to text labels or PNG icons. For now (operator desktop browser), emoji-only is fine.
- **OEE per-device gauges are statically declared in HTML.** After Task 1, the gauges for PLC-CNC-006 and PLC-PAINT-007 must be added to `index.html` to render. Confirmed not an auto-discovery surface today — if this becomes painful at 10+ devices, we will revisit and dynamically render gauges from a registered-devices snapshot. Not in scope for this spec.
- **Subsequence search noise (Task 3):** `wld` also subsequence-matches `Welcome to the dashboard`. For the typical Event Log content this should not be a real-world problem, but if it gets noisy in practice we can switch to substring-only or add a per-character min-density threshold.
