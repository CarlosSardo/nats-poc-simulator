# Lambert — History

## Project Context
- **Project:** nats-poc — A NATS-based downtime detector for simulated PLC devices
- **Stack:** .NET / C#, NATS messaging
- **User:** Carlos Sardo
- **Goal:** Upskilling project to learn NATS. Simulate PLC device data streams, detect device up/down status.

## Learnings
- **SignalR Hub testing pattern:** Mock `IHubCallerClients` and set `hub.Clients = mock` directly on the hub instance. Verify calls via `IClientProxy.SendCoreAsync` (the underlying method that `SendAsync` routes to) using NSubstitute's `Arg.Is<object?[]>` for argument matching.
- **NSubstitute for SignalR:** Use `Substitute.For<IClientProxy>()` for the caller proxy and `mockClients.Caller.Returns(callerProxy)` to wire it up. This avoids needing a real SignalR server.
- **Real DeviceTracker in hub tests:** Since DeviceTracker is simple domain logic with no external dependencies, using a real instance (not a mock) in hub tests gives more realistic coverage and avoids over-mocking.
- **Test naming convention:** Following existing pattern of `MethodUnderTest_Scenario_ExpectedBehavior` from DeviceTrackerTests.cs.
- **In-memory SQLite for EF Core tests:** Use `SqliteConnection("DataSource=:memory:")` kept open for the test lifetime, shared across all DbContext instances via `UseSqlite(connection)`. Dispose the connection in `IDisposable.Dispose()`.
- **SQLite does NOT support ORDER BY on DateTimeOffset:** EF Core's SQLite provider throws `NotSupportedException` for `OrderByDescending(r => r.StartedAt)` when `StartedAt` is `DateTimeOffset`. Workarounds: order by `Id` (auto-increment) server-side, or use `.AsEnumerable()` for client-side ordering. Found this bug in Dallas's production code during testing.
- **Real ServiceCollection for BackgroundService tests:** When testing services that use `IServiceScopeFactory` to resolve scoped DbContexts, use a real `ServiceCollection` + `AddDbContext<T>(o => o.UseSqlite(connection))` + `BuildServiceProvider()` instead of mocking. Mocked `IServiceProvider.GetService()` has issues with `GetRequiredService<T>()` extension method resolution.
- **Tracker timeout sizing for integration tests:** Use a large tracker timeout (30s) with old heartbeats (60s ago) so recovery heartbeats don't re-timeout during the test. A 2s timeout causes the recovery heartbeat to expire before assertions run.
- **CapturePayload must filter by method name:** When a SignalR hub sends multiple messages in `OnConnectedAsync`, test helpers using `.ReceivedCalls().Single()` break. Filter by the method name argument: `.Single(c => (string)c.GetArguments()[0]! == "ReceiveAllStatuses")`.

## Cross-Agent Updates (2026-03-10)
- **Dallas** added `DowntimeDbContext` constructor param to `DashboardHub` — required updating existing `DashboardHubTests.cs`.
- **Dallas** confirmed `IServiceScopeFactory` pattern for DB access from singleton BackgroundService — Lambert validated this works with real `ServiceCollection` in tests.
