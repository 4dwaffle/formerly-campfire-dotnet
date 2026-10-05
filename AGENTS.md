# Campfire .NET port

Port the pinned Rails Campfire behavior. Reference checkouts are read-only. Do not commit, push, publish, or contact anyone unless the parent assigns that action.

Use .NET 10 and ASP.NET Core Minimal APIs. Keep frontend assets and HTTP contracts compatible. Use the existing SQLite schema and Active Storage layout. Read the original Ruby for business rules. Record unimplemented behavior and deliberate differences honestly; do not use placeholders to claim parity.

Each agent owns its assigned feature directories and tests. Shared contracts in src/Campfire/Contracts are parent-owned: request changes through messages. Do not change the host, project file, or other agents' files without coordination. Tests must exercise meaningful behavior, especially permissions, compatibility, and transaction side effects.

Build with dotnet build -c Release. Keep all SQL parameterized. HTTP output must escape untrusted text and sanitize rich HTML. Benchmark results must include actual responses, error counts, and raw measurements; no hardcoded fixture response shortcuts.
