# Testbed Server Source

Standalone .NET server stack and administration dashboard for the MMO testbed.

This repository is the source backup for the current standalone server architecture. It contains the authoritative GameServer, GatewayServer, shared contracts/domain/application code, networking support, server-side tests, and the MMO Dashboard used to operate and author server configuration/content.

It does **not** contain the Unity client project or commercial Unity/Synty asset content.

## Origins and why this server exists

This server did not begin as a disconnected rewrite. It grew directly out of the optimization, networking, authority, and scalability work done on the original uMMORPG-based project.

A large amount of work was first done inside uMMORPG to reduce bandwidth, improve AOI/replication behavior, separate gameplay authority from presentation, harden persistence and validation, and remove unnecessary runtime cost. That work proved that the existing game could be pushed significantly further than its original architecture suggested.

Eventually, however, the limiting problem was no longer an individual gameplay system. The authoritative server was still running inside Unity.

That created an architectural boundary that was too easy to blur. Client and server code lived close enough together that Unity/runtime assumptions, presentation concerns, and client-oriented code could accidentally leak into server-side systems, while server concerns could also become entangled with client code. Even with disciplined separation, the server was still fundamentally hosted by a game engine that was designed to run much more than an MMO simulation service actually needs.

Mirror was useful during that stage and provided the networking foundation for the Unity-hosted server, but it also carried framework coupling and protocol/header overhead that became increasingly important as the project was optimized for dense MMO replication and large concurrent populations.

The next step was therefore to replace the networking layer with a leaner transport/protocol path designed around the requirements of this project rather than continuing to carry the full Mirror/Unity networking model.

Once that boundary had been crossed, the larger architectural decision became straightforward: move the authoritative runtime completely out of Unity.

The result is the current standalone **.NET 8 server**. Unity remains the client and content-authoring environment, while authoritative simulation, validation, networking, persistence coordination, and server runtime systems live in ordinary .NET projects with a much harder client/server separation.

The standalone server should therefore be viewed as the continuation of the same MMO project after the uMMORPG work exposed the architectural ceiling of keeping the authoritative server inside Unity.

## Repository layout

```text
Game.Server.Application/   Gameplay/application services
Game.Server.Domain/        Server domain/runtime models
Game.Shared/               Shared contracts, protocol types, and data definitions
GameServer/                Standalone authoritative game server executable
GatewayServer/             Authentication, persistence, content/backend gateway
LiteNetLib.Server/         Server networking/transport support
MMODashboard/              Server administration/configuration dashboard
Tests/                     Server-side regression and contract tests
```

## MMO Dashboard

`MMODashboard/` is the administration and authoring tool for the standalone server stack.

The current dashboard is a Windows/WPF application built around a local-first administration boundary. Server documents are accessed through `IServerAdminWorkspace` rather than having the UI directly own every filesystem operation. The current local workspace edits the canonical server files, while an HTTP workspace implements the same contract for a future authenticated remote administration endpoint.

The dashboard currently includes infrastructure for:

- reading and writing server runtime configuration;
- editing Gateway settings;
- editing canonical gameplay content;
- optimistic concurrency/version checks so an older edit does not silently overwrite a newer one;
- atomic local writes with backup/last-known-good behavior;
- launching and monitoring existing server maintenance/startup processes;
- moving toward remote administration without requiring a second parallel Dashboard UI architecture.

The remote administration contract is intended to use authenticated HTTPS endpoints and must remain strongly authorized. The Dashboard is an administrative tool, not a normal game-client interface.

## Technology

- .NET 8
- C#
- Standalone authoritative server architecture
- LiteNetLib-based server networking
- SQLite-backed Gateway persistence
- DotRecast/Detour support in the GameServer
- WPF-based MMO Dashboard
- No Unity runtime dependency in the standalone server projects

## Build

Requires the .NET 8 SDK.

Build the GameServer:

```bash
dotnet build GameServer/GameServer.csproj
```

Build the GatewayServer:

```bash
dotnet build GatewayServer/GatewayServer.csproj
```

Build the Dashboard:

```bash
dotnet build MMODashboard/MMODashboard.csproj
```

## Authority model

The server is authoritative for gameplay state and validation.

Server-authoritative does **not** mean that the client should send arbitrary actions and rely on the server to reject them. The intended model is:

- the client uses the public state and definitions it already has to present only actions it believes are valid;
- the client locally suppresses obviously invalid requests where practical;
- the server independently validates every authoritative action because the client cannot be trusted;
- authoritative gameplay state, ownership, combat, inventory, persistence, and world decisions remain server-owned.

This keeps unnecessary traffic and rejection work down without weakening authority.

## Current scalability reference

Primary clean measured baseline — **September 13, 2026**:

- Hardware: Intel Core i7-3770 @ 3.40 GHz
- 4 cores / 8 logical processors
- 16 GB RAM
- Test type: **dense AOI**, with substantial observer/replication fan-out pressure
- Whole-machine CPU at the clean 600-CCU measurement: approximately **32%**
- Whole-machine RAM: approximately **5.3 / 16.0 GB**
- Outbound network: approximately **22.7 Mbit/s**
- Inbound network: approximately **7.5 Mbit/s**

The later test reached **800+ CCU** while the GameServer was still running with additional headroom. The limiting factor became the bot-generator machine rather than the GameServer, so 800+ CCU should not be treated as the measured GameServer ceiling.

## Source-control purpose

`main` should represent the current working standalone server baseline, including the Dashboard when it changes alongside the server stack.

For meaningful validated milestones, use clear commit messages and/or tags so regressions can be compared against known-good points.

Recommended examples:

```text
baseline-2026-09-15
code-health-v1
runtime-scheduler-health-v1
```

## Important repository rules

Do not commit runtime secrets or generated state.

Examples that should remain outside source control:

- passwords, tokens, private keys, or certificates;
- production databases and runtime SQLite state;
- `bin/` and `obj/` output;
- logs, dumps, profiler captures, and temporary runtime files unless intentionally added as test evidence.

## License and contributions

This project is released under the **MIT License**. See [`LICENSE`](LICENSE).

Forks, experiments, fixes, performance work, tooling, and other extensions are welcome. If you build on this project, preserve the MIT license notice for the portions covered by this repository.

Third-party components and bundled dependencies remain subject to their own license terms where applicable.

## Status

This repository is an active development/testbed snapshot rather than a packaged public release. Architecture, protocol contracts, performance work, gameplay systems, and administration tooling are still evolving.
