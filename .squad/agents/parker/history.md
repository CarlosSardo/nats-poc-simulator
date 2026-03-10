# Parker — History

## Project Context
- **Project:** nats-poc — NATS-based downtime detector for simulated PLC devices
- **Stack:** .NET / C#, NATS messaging
- **User:** Carlos Sardo
- **Joined:** 2026-03-10

## Learnings
- **Dark SCADA theme**: Used `#0f0f1a` / `#1a1a2e` / `#16213e` layered backgrounds for depth — industrial control-room aesthetic.
- **No framework, no build step**: Vanilla HTML/CSS/JS keeps frontend deps at zero; SignalR loaded from CDN.
- **Pulse animation on heartbeat**: CSS `@keyframes pulse-green` with a forced reflow trick (`void card.offsetWidth`) to restart the animation on every event.
- **1-second ticker**: `setInterval` refreshes relative "last seen" timestamps and running downtime counters without waiting for the next heartbeat.
- **SignalR reconnect**: Used `withAutomaticReconnect` with exponential backoff (capped at 30s), plus a manual fallback in `onclose` for resilience.
- **Event log FIFO**: Insert newest entry at top, trim from bottom when exceeding 50 entries — keeps DOM lightweight.
- **Contract alignment**: JS handler signatures match the agreed `ReceiveHeartbeat(deviceId, isUp, lastSeen, downSince, temperature, pressure, isRunning)` and `ReceiveAllStatuses(statuses)` hub methods exactly.
- **Downtime history panel**: Added between device-grid and event-log. Three SignalR events: `ReceiveDowntimeHistory` (bulk load on connect), `ReceiveDowntimeStarted` (new active row), `ReceiveDowntimeResolved` (in-place row update). Contract uses `id`, `deviceId`, `deviceName`, `startedAt`, `endedAt`, `durationSeconds`, `reason`, `isResolved`.
- **Client-side filtering**: Device filter tabs are built dynamically from downtime records. Filtering is instant (no API call) — just re-renders from the in-memory `downtimeRecords` array.
- **Active downtime ticking**: Integrated into the existing 1-second `setInterval`; `tickActiveDts()` updates duration cells and stats for unresolved rows.
- **In-place DOM updates**: `ReceiveDowntimeResolved` finds the existing `<tr>` by `data-dt-id` attribute and patches cells directly — no full re-render, smooth UX.
- **Row animation**: CSS `dt-row-enter` keyframe gives new rows a subtle slide-in; active rows get a red-tinted background and a pulsing dot badge.
- **XSS safety**: `escapeHtml()` via DOM text node for device names/IDs in table cells.
- **Key file paths**: `wwwroot/index.html` (downtime section), `wwwroot/css/dashboard.css` (downtime styles), `wwwroot/js/dashboard.js` (downtime handlers + state).

## Cross-Agent Updates (2026-03-10)
- **Dallas** provided 3 SignalR events and REST endpoint for downtime data — contract honored exactly.
- **Lambert** found and fixed a SQLite DateTimeOffset ordering bug in Dallas's backend code that would have affected the history data Parker displays.
