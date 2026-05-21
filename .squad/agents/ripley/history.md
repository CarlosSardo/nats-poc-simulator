# Ripley — History

## Project Context
- **Project:** nats-poc — A NATS-based downtime detector for simulated PLC devices
- **Stack:** .NET / C#, NATS messaging
- **User:** Carlos Sardo
- **Goal:** Upskilling project to learn NATS. Simulate PLC device data streams, detect device up/down status.

## Learnings
- **2026-03-10 — Dashboard architecture decision:** Added NatsPoc.Dashboard as an ASP.NET Core 8 + SignalR web app. Chose vanilla HTML/CSS/JS over a SPA framework to keep complexity low for an upskilling project. SignalR hub at `/hubs/dashboard` pushes heartbeat updates and full status snapshots to browser clients. Project nested under the existing `src` solution folder, added to docker-compose with NATS dependency.

## Cross-Agent Updates (2026-03-10)
- **Dallas** extended Dashboard with SQLite downtime persistence (EF Core + `downtime.db`). CORS note: `SetIsOriginAllowed(_ => true)` is dev-only — needs production lockdown per Ripley's architecture guidance.
- **Parker** built the downtime history UI panel using the vanilla JS approach Ripley specified.
- **Lambert** wrote 9 downtime tests (25/25 pass), found SQLite DateTimeOffset ordering bug. Test count grew from 16 → 25.
- **2026-03-10 — README rewrite:** Rewrote README.md from scratch to reflect current architecture (NATS + Simulator + Dashboard in Docker Compose, standalone Detector for dev). Corrected docker-compose to 3 services (not 4 — Detector is not containerized). Documented all REST endpoints, config vars (Nats__Url, Detector__TimeoutSeconds, etc.), OEE formula, NATS concepts, project structure, simulated device table, and test instructions. Primary path: `docker compose up --build` → http://localhost:5050.
- **2026-03-10 — .NET 10 docs update:** Updated all .NET 8 references in README.md to .NET 10 after the runtime upgrade. Five changes: badge (text + shield URL + download link), description paragraph, prerequisites SDK link, Tech Stack runtime row, Tech Stack EF Core row. CONTRIBUTING.md had no version-specific references — no changes needed. Verified zero stale ".NET 8" strings remain.
- **2026-05-21 — Specs live under `specs/`:** Created `specs/` directory and authored `SPEC-DEMO-02` (`specs/spec-demo-02.md`) — four-task spec covering two new simulated PLCs (Dallas), per-device emoji identity (Parker), fuzzy Event Log search (Parker), and ASCII→Mermaid architecture diagram consolidation (Ripley). Future feature specs follow the same `specs/spec-<slug>.md` convention.
- **2026-05-21 — SPEC-DEMO-02 Task 4 done — single-source Mermaid architecture:** Replaced the dual ASCII+Mermaid blocks under `## 🏗️ Architecture` in `README.md` with a single `flowchart TB` Mermaid diagram. Listed all 7 PLCs individually (PRESS, CONV, WELD, PACK, OVEN, CNC, PAINT) — 7 nodes still readable, no need to collapse. Used concrete service names from the codebase (`NatsHeartbeatService`, `DeviceTracker`, `OeeCalculationService`, `DashboardHub`, `DowntimeDbContext`, `DowntimeDetectorWorker`) instead of generic "NATS Subscriber" / "OEE Calculator" labels. Switched `\n` line breaks to `<br/>` for safer GitHub rendering, and HTML-escaped `&` to `&amp;` inside the one edge label that needed it. Four subgraphs (Simulator, NATSServer, Dashboard, Detector) + Browser as a top-level node. No other README sections touched.

### 2026-05-21 - team update: SPEC-DEMO-02 shipped
All four tasks complete. Dallas (Task 1) and Ripley (Task 4) approved by Ash. Parker shipped Tasks 2 + 3. Lambert added 8 tests + flagged ThermalDrift/BurstReject seam gap for next-session decision.
