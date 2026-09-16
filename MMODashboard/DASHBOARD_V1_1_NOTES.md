# MMO Dashboard 1.1 - local admin foundation

This pass keeps the dashboard local-first while separating the UI from the storage/transport layer.

## Added

- `IServerAdminWorkspace` boundary for all authoring operations.
- `LocalServerAdminWorkspace` for canonical local server files.
- `HttpServerAdminWorkspace` scaffold for the future authenticated remote admin endpoint.
- Optimistic concurrency tokens so the dashboard does not silently overwrite a file changed by another editor.
- Atomic local writes with a `.dashboard.bak` last-good backup.
- Server Config tab:
  - `Config/ServerConfig.bat` variables as editable rows.
  - Gateway `appsettings.json` values as editable rows instead of raw JSON.
  - validation for current Gateway option ranges.
- Item Builder tab:
  - new/duplicate/edit item workflow against `Content/GameplayContent.json`.
  - automatic content revision bump on save.
  - equipment-slot choices loaded from the server content itself.
  - resource and status-effect choices loaded from the server content itself.
  - stat modifier and use-effect editing.
  - current server item validation rules for IDs, stacks, presentation IDs, combat values, slot references, starter quantities, resources/status effects, and consumable shape.
  - stable existing item Definition IDs cannot be renamed.
  - restart-required warning when an existing item's hot-reload-protected structure changes.

## Remote-ready boundary

See `ADMIN_API_CONTRACT.md`. The authoring screens do not know whether their documents come from disk or HTTP. When a secure server-side admin endpoint is added, the dashboard can switch workspace implementations instead of rebuilding the item/config UI.

## Canonical files

The dashboard intentionally does not create a separate dashboard data format. It edits the files the current standalone server already owns:

- `Config/ServerConfig.bat`
- `Source/GatewayServer/appsettings.json` for a developer tree (deployed Gateway copy is fallback)
- `Content/GameplayContent.json`
