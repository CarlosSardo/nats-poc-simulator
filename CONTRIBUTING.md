# Contributing

Thanks for your interest in contributing! This project is a demo/learning tool, but contributions are welcome.

## Getting Started

1. Fork the repository
2. Clone your fork
3. Create a feature branch: `git checkout -b feat/my-feature`
4. Make your changes
5. Run the tests: `dotnet test`
6. Commit using [Conventional Commits](https://www.conventionalcommits.org/):
   ```
   feat: add new feature
   fix: resolve bug in device tracker
   docs: update README
   test: add OEE calculation tests
   chore: update dependencies
   refactor: simplify heartbeat processing
   ```
7. Push and open a Pull Request

## Commit Message Format

All commits **must** follow the [Conventional Commits](https://www.conventionalcommits.org/) specification:

```
<type>: <description>

[optional body]
```

**Types:** `feat`, `fix`, `docs`, `test`, `chore`, `refactor`, `style`, `ci`

## Running Locally

```bash
# Full stack via Docker
docker compose up --build

# Or run services individually against NATS in Docker
docker compose up nats -d
dotnet run --project src/NatsPoc.Dashboard
dotnet run --project src/NatsPoc.PlcSimulator
```

## Code Style

- Follow existing .NET conventions in the codebase
- Keep things simple — this is a PoC, not production software

## License

By contributing, you agree that your contributions will be licensed under the MIT License.
