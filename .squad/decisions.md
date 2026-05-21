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

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
