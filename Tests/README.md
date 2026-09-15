# Standalone Server Regression Tests

These are dependency-free console regression runners. They intentionally avoid adding a test framework package to the production dependency graph.

Run after a normal Release compile:

```powershell
dotnet run --project .\Tests\Game.Server.Application.RegressionTests\Game.Server.Application.RegressionTests.csproj -c Release
dotnet run --project .\Tests\GatewayServer.RegressionTests\GatewayServer.RegressionTests.csproj -c Release
```

Coverage added by this patch:

- lost/failed HTTP response after a committed item mutation reconciles through the existing authoritative player-systems load path;
- HttpClient-style `OperationCanceledException` timeout is distinguished from caller cancellation;
- stale item mutations reload and install authoritative state without claiming the rejected request succeeded;
- GameServer transient world-item ids remain in the reserved high range;
- empty legacy persisted JSON retains canonical defaults;
- malformed, `null`, and structurally invalid non-empty persisted character state fails closed instead of silently substituting defaults.
