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

`MMODashboard/` is the primary local developer/operator interface for the standalone server stack.

The normal workflow is **compile the Dashboard first, then use the Dashboard to configure, build, start, stop, and monitor the GatewayServer and GameServer**. Raw command-line builds remain available for troubleshooting and automation, but they are not the intended day-to-day setup path.

The Dashboard works directly with the canonical server-owned files rather than maintaining a second configuration database:

- `Config/ServerConfig.bat` — runtime paths, ports, backend connection, GameServer options, and optional map/staff settings;
- `Source/GatewayServer/appsettings.json` — Gateway settings in a developer source tree (with the deployed Gateway copy as fallback);
- `Content/GameplayContent.json` — canonical gameplay/content definitions.

Current Dashboard capabilities include:

- **BUILD ALL** — builds GatewayServer first and GameServer second, stopping on a failed build;
- **BUILD & START** — builds both servers, then starts the stack;
- individual GatewayServer and GameServer **Build / Run / Stop** controls;
- automatic detection of the existing build and runtime BAT files;
- installation/repair of the local development `Config/ServerConfig.bat`;
- direct editing and validation of runtime configuration and Gateway settings;
- direct editing of canonical gameplay item definitions, including revision updates and restart-required warnings;
- captured build/runtime stdout and stderr in the Operations log;
- running/stopped status for GatewayServer and GameServer;
- optimistic concurrency checks for authored documents;
- atomic local writes with a last-good backup.

When starting the stack, the Dashboard starts **GatewayServer first** and then GameServer. Runtime launches read the existing `ServerConfig.bat` and start the built executables with the server's existing supported command-line configuration. This keeps one canonical runtime configuration path instead of introducing a Dashboard-specific launcher protocol.

The administration workspace is local-first. `IServerAdminWorkspace` separates the UI from storage/transport so a future authenticated remote administration endpoint can reuse the same authoring UI without creating a parallel Dashboard architecture. Remote administration is not required for the normal local developer workflow.

## Fresh-machine setup

### 1. Install prerequisites

For the standalone server repository and Dashboard, install:

- **Windows x64** for the current WPF Dashboard workflow;
- **.NET 8 SDK** available on `PATH`;
- **Git** if cloning the repository with Git.

Verify the SDK:

```bat
dotnet --version
```

The Dashboard build script will stop with an error if the .NET 8 SDK cannot be found.

### 2. Clone or download the repository

Clone the repository and keep its directory structure intact.

```bat
git clone https://github.com/Riryan/Testbed-Server-Source.git
cd Testbed-Server-Source
```

The Dashboard resolves the server root from the repository layout. Do not copy only the Dashboard executable to an unrelated directory. The server root must contain the source/server folders it operates.

### 3. Compile the MMO Dashboard

From the repository root run:

```bat
MMODashboard\BuildDashboard.bat
```

The script publishes a Windows x64, self-contained, single-file Dashboard:

```text
MMODashboard\Publish\MMODashboard.exe
```

Internally this uses:

```bat
dotnet publish "MMODashboard.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "Publish"
```

Because the published Dashboard is self-contained, the .NET SDK is needed to **build** it; the published executable itself carries its runtime.

Run the Dashboard from its location inside the repository/server tree so it can resolve the main server root.

### 4. Open Setup / Configuration

On first launch the Dashboard attempts to auto-detect the existing Gateway/GameServer build and run scripts.

If the environment is incomplete, expand **Setup / Configuration** and use **Auto Detect**.

The Dashboard expects four operational scripts:

- Build GatewayServer;
- Build GameServer;
- Run GatewayServer;
- Run GameServer.

The normal repository layout is detected automatically.

### 5. Install or repair the runtime configuration

The servers require:

```text
Config\ServerConfig.bat
```

If it is missing, use **Install/Repair** next to Runtime Config in the Dashboard.

The development configuration defines the canonical local runtime values such as:

- Gateway public HTTPS port;
- Gateway internal backend port;
- GameServer UDP port;
- GameServer connect key;
- Gateway internal backend address;
- GameServer backend authentication key file;
- optional server identity/advertisement settings;
- optional map-data directory;
- whether baked map data is required;
- optional staff authorization/audit files.

Review these values in the Dashboard before using the environment outside local development.

### 6. Review Gateway and server configuration

Use the Dashboard's configuration screens rather than creating a second local configuration system.

The **Server Config** editor modifies `Config/ServerConfig.bat`.

The **Gateway Settings** editor modifies the source `GatewayServer/appsettings.json` in a developer tree so changes survive the next Gateway build.

Saved runtime configuration changes require affected running processes to be restarted. Gateway source-setting changes should be followed by a Gateway build and restart when appropriate.

Do not commit runtime secrets, generated keys, certificates, or production database state.

### 7. Build GatewayServer and GameServer

For the normal workflow, click:

**BUILD ALL**

The Dashboard runs the existing Gateway build first. If it succeeds, it builds GameServer. A failed Gateway build prevents the GameServer build sequence from continuing.

Build output and errors appear in the Dashboard Operations log.

You can also use the individual **Build** button on either server panel while developing one component.

### 8. Start the server stack

After successful builds, click:

**BUILD & START**

to rebuild and start the complete stack, or use the individual **Run** controls if the executables are already current.

The Dashboard starts GatewayServer before GameServer. It reads `ServerConfig.bat` and starts the executables using the configured ports/backend settings.

The default local development ports are currently:

```text
Gateway HTTPS:    8443
Gateway internal: 8444
GameServer UDP:   7777
```

The Dashboard status indicators refresh automatically and the Operations log receives server stdout/stderr.

### 9. Stop or restart services

Use the individual **Stop** controls to stop GatewayServer or GameServer.

Configuration changes that affect startup behavior require the affected process to be restarted. Gameplay-content editing may be hot-reloadable depending on the change; the Item Builder explicitly warns when a structural item change requires a Gateway/GameServer restart.

## Gameplay content authoring

The Dashboard Item Builder edits the canonical `Content/GameplayContent.json`; it does not create a Dashboard-only item database.

It supports creating, duplicating, and editing item definitions while validating the current server rules for stable IDs, presentation IDs, stacks, durability, equipment slots, combat values, resources, status effects, starter quantities, and consumable structure.

Saving an item increments the gameplay-content revision automatically. Existing stable Definition IDs cannot be renamed. When a change affects a structure that the live content-reload path does not safely replace, the Dashboard reports that a restart is required.

## Manual build / troubleshooting

The Dashboard is the normal build and operation interface. These commands are useful when diagnosing a build outside the Dashboard.

Build the GatewayServer directly:

```bat
dotnet build GatewayServer\GatewayServer.csproj
```

Build the GameServer directly:

```bat
dotnet build GameServer\GameServer.csproj
```

Build the Dashboard project without publishing:

```bat
dotnet build MMODashboard\MMODashboard.csproj
```

To produce the normal distributable Dashboard executable, prefer:

```bat
MMODashboard\BuildDashboard.bat
```

If the Dashboard cannot start, first verify that it remains inside a valid server/repository tree and that the expected build/run scripts are present. If the Dashboard can open but cannot start the servers, verify `Config/ServerConfig.bat` and use **Install/Repair** if it is missing.

## Technology

- .NET 8
- C#
- Standalone authoritative server architecture
- LiteNetLib-based server networking
- SQLite-backed Gateway persistence
- DotRecast/Detour support in the GameServer
- WPF-based MMO Dashboard
- No Unity runtime dependency in the standalone server projects

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
