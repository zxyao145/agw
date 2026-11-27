---
title: "Build Server and Desktop extensions"
description: "Author, package, and debug loadable extensions with Agw.Extensions.Sdk and @agw/extension-sdk."
weight: 45
lastmod: 2026-11-26
translationKey: docs/development/extension-sdk
---

Beyond built-in Tools, ToolBlocks, and Integration plugins, AGW supports loadable extension packages for both Server and Desktop. This guide covers the two extension SDKs, manifest formats, registration APIs, combined packaging, and local debugging.

Before starting, review [Architecture]({{< relref "/docs/development/architecture" >}}) and [Extend tools and integrations]({{< relref "/docs/development/extensions" >}}). For host installation directories, restart coordination, and administrator enablement, see [Server extensions and ACP hosts]({{< relref "/docs/operations/extensions" >}}).

## Three extension shapes

| Shape | `manifestVersion` | `targets` | Runtime | Typical use |
| --- | --- | --- | --- | --- |
| Desktop extension | `1` | Omitted | Isolated `WebContentsView` session under Desktop `ExtensionHost` | File viewers, main and sidebar views, Ribbon items, themes, chat and repository helpers |
| Server extension | `2` | `1` | Isolated collectible `AssemblyLoadContext` inside the Server process | Custom HTTP endpoints, extension Tools and Skills, event handlers, notification channels, background services, and extension-owned tables |
| Combined Server/Desktop extension | `2` | `3` | Both Server and Desktop | Full workflow extensions that need server-side persistence or background processing paired with dedicated Desktop views (such as TaskPilot and Feishu) |

Every extension uses a lowercase GUID v7 as its stable `id`.

## Server extensions (`Agw.Extensions.Sdk`)

A Server extension is a self-contained .NET assembly folder placed in its own subdirectory under `Extensions:Directory` (default `./Extensions`), with `manifest.json` at its root and every declared file present. Each Host loads an enabled extension generation into a collectible `AssemblyLoadContext`; updates and removals take effect on the next startup after all Hosts stop.

### Manifest (`manifest.json`)

```json
{
  "manifestVersion": 2,
  "id": "019a4c17-5f00-7a80-8b3d-2b732f48a101",
  "name": "taskpilot",
  "displayName": "TaskPilot",
  "version": "1.0.0",
  "description": "Rules, matters, reviews and automated delivery managed through TaskPilot.",
  "author": "Agw",
  "targets": 1,
  "server": {
    "apiVersion": 1,
    "entryAssembly": "server/Agw.Extensions.TaskPilot.dll",
    "entryType": "Agw.Extensions.TaskPilot.TaskPilotExtension",
    "permissions": {
      "events": {
        "publish": ["taskpilot.*"],
        "publishRoot": true
      }
    }
  },
  "files": {
    "server/Agw.Extensions.TaskPilot.dll": "<sha256>"
  }
}
```

- `entryAssembly` and `entryType` identify the assembly and full type name implementing `IServerExtension`.
- `files` lists relative paths inside the package and their SHA-256 digests. Hosts verify the manifest and file digests at startup and refuse to start on a mismatch.
- Extensions that publish custom events declare allowed event-type prefixes under `server.permissions.events`.

### Implement `IServerExtension` and JSON metadata

Reference `Agw.Extensions.Sdk` and implement `IServerExtension` (`ConfigureServices`, `StartAsync`, and `StopAsync`).

Server extensions must register their own source-generated `JsonSerializerContext`. The runtime rejects collectible extension types that lack registered generated metadata, including extension types nested in arrays or generic types. Endpoint serialization and `IExtensionTool.CreateFunction(JsonSerializerOptions)` share the same generation-owned `JsonOptions`.

```csharp
using System.Text.Json.Serialization;
using Agw.Extensions.Contracts;
using Agw.Extensions.Sdk;
using Microsoft.Extensions.DependencyInjection;

[JsonSerializable(typeof(CalendarRequest))]
[JsonSerializable(typeof(CalendarResponse))]
internal partial class CalendarJsonContext : JsonSerializerContext { }

public sealed class CalendarExtension : IServerExtension
{
    public void ConfigureServices(IServiceCollection services, IExtensionRegistration registration)
    {
        registration.RegisterJsonContext(CalendarJsonContext.Default);
        registration.RegisterEndpoint(
            new ExtensionEndpointDescriptor
            {
                Id = "events-list",
                Method = "GET",
                RelativePath = "events/list",
                Summary = "List calendar events",
            },
            static (provider, context, request, cancellationToken) =>
                ValueTask.FromResult(ExtensionEndpointResponse.OkJson(new CalendarResponse(), context.JsonOptions))
        );
    }

    public ValueTask StartAsync(IExtensionContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public ValueTask StopAsync(IExtensionContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
```

### Register capabilities with `IExtensionRegistration`

| Method | Purpose |
| --- | --- |
| `RegisterJsonContext` | Register source-generated `JsonSerializerContext` metadata |
| `RegisterEndpoint` | Register HTTP endpoints mounted under `/api/ext/<name>/<RelativePath>` |
| `RegisterTool<TTool>` | Register an `IExtensionTool` with explicit `Name`, `RequiredPermission`, and `AllowInPlanMode` |
| `RegisterSkill` | Register a Skill descriptor and content provider; enabled extensions contribute Skills automatically during capability composition without creating `skill` table rows or manual import |
| `RegisterEventHandler` | Register a handler receiving matching `ExtensionEventDelivery` events |
| `RegisterEventSourceProvider<TProvider>` | Register a custom event source provider |
| `RegisterNotificationChannel<TChannel>` | Register a notification channel (for example Feishu) |
| `RegisterOutboxResultHandler<THandler>` | Register an outbox result handler by `kind` |
| `RegisterBackgroundService<TService>` | Register a background service scoped to the extension generation |
| `RegisterDatabase` / `RegisterDbContext` | Register an extension-owned database context and migrations |

At runtime, `IExtensionContext` exposes the extension identity (`ExtensionId`, `GenerationId`, `Epoch`, `ReleaseHash`), `DataDirectory`, `CurrentUser`, `Leases`, `Secrets`, `JsonOptions`, `Logger`, and `HostCapabilities`.

### Extension-owned tables with EF Core (`Agw.Extensions.Sdk.EntityFrameworkCore`)

Server extensions that own business tables reference `Agw.Extensions.Sdk.EntityFrameworkCore` and include their migrations assembly and provider dependencies in the package:

1. Call `registration.RegisterDbContext<CalendarDbContext, CalendarMigrationsMarker>()` in `ConfigureServices`.
2. Subclass `ExtensionDbContext` and map entities in `ConfigureExtensionModel`. Every table entity implements `IExtensionOwnedEntity` (`CreateBy`, `CreateTime`, `UpdateBy`, `UpdateTime`); child rows can implement `IExtensionOwnedChild<TRoot, TRootKey>`.
3. Use short lowercase `snake_case` table names; the runtime prefixes them with `zext_<extensionIdN>_`. Queries are filtered to the current user and return no rows without a user context. The SDK rejects `IgnoreQueryFilters`, `ExecuteUpdate`, `ExecuteDelete`, raw SQL queries or commands, and external transactions.
4. Include separate SQLite and PostgreSQL migration and snapshot classes in the migrations assembly, annotated with `[ExtensionMigrationProvider(ExtensionDatabaseProvider.Sqlite)]` or `[ExtensionMigrationProvider(ExtensionDatabaseProvider.Postgres)]`. Configure the design-time factory with `UseSnakeCaseNamingConvention()` and replace `IMigrationsAssembly` with `ExtensionMigrationsAssembly`.
5. Migrations cannot contain raw SQL, foreign keys, sequences, check constraints, or computed SQL. Use `ExtensionUpdateAllRowsOperation` to assign uniform values across every row of an extension-owned business table.

Business transactions hold a shared database lock and validate the active local generation before execution and commit; migrations hold the matching exclusive lock for the full migration batch on one open connection. Retained business tables and migration history survive updates, so extension authors own schema and data compatibility across releases.

## Desktop extensions (`@agw/extension-sdk`)

Desktop extensions run inside Electron's `ExtensionHost`. Each extension has an isolated session (`persist:agw-extension-<id>`), and each background script, sidebar view, main view, and settings page runs in its own `WebContentsView`. Any front-end stack that builds to static files works (plain JS, React, Vue, WASM, etc.).

### Package layout and `manifest.json`

An extension package lives in a directory named after its `id`, with `manifest.json` at the root. All manifest paths are relative POSIX paths inside the package, and `__agw/` is reserved by the host. `id` must be a lowercase GUID v7, and `name` (trimmed, case-insensitively) cannot be `host`.

Example file-viewer manifest:

```json
{
  "manifestVersion": 1,
  "id": "01a173b5-526c-74d8-864c-0e2da2d0a21c",
  "name": "Excalidraw",
  "version": "1.0.0",
  "description": "Opens and edits .excalidraw files in the Files view.",
  "author": "Agw",
  "icon": "icon.svg",
  "contributes": {
    "fileViewers": [
      {
        "id": "editor",
        "title": "Excalidraw",
        "icon": "PenTool",
        "patterns": ["*.excalidraw"],
        "entry": "index.html"
      }
    ]
  },
  "permissions": ["fs.cloud.read", "fs.cloud.write"]
}
```

Supported `contributes` entries:

- `ribbon`: Ribbon icons that toggle a sidebar view or open a main view.
- `sidebarViews`: Left sidebar views.
- `mainViews`: Main workspace tab views.
- `fileViewers`: File viewers matched by case-insensitive glob `patterns` (`*`, `?`), with optional `priority` (`default` or `option`).
- `settings`: Extension settings page shown under **Settings → Extensions**.
- `themes`: JSON or CSS theme files (`{ id, title, path }`).
- `background`: Background script entry point.

### Connect the SDK and author views

Import types and helpers from `@agw/extension-sdk` at development time, and configure your bundler to mark `@agw/extension-sdk` external with the runtime path `/__agw/sdk.js` (served at `agw-extension://<id>/__agw/sdk.js`, with types at `__agw/sdk.d.ts`). You can also export the SDK type definitions from Desktop **Settings → Extensions** for standalone repositories.

Views use `defineView`, background scripts use `defineBackground`, and pages can call `connect()` directly to obtain `ExtensionApi`. Example based on the bundled `image-viewer` extension:

```tsx
import { defineView, type ExtensionApi, type FileViewerParams } from "@agw/extension-sdk";

let extensionApi: ExtensionApi | null = null;

defineView({
  onLoad(api, params) {
    api.theme.apply();
    extensionApi = api;
    renderFile(api, params as FileViewerParams);
  },
  onShow(params) {
    if (extensionApi) {
      renderFile(extensionApi, params as FileViewerParams);
    }
  },
  onHide() {
    // Pause work or release transient state when the tab is hidden
  },
  onUnload() {
    cleanup();
  },
});
```

### Core `ExtensionApi` surfaces

- **Workspace and tabs (`workspace`)**: `openMainView(id, params?, options?)` opens or activates a main view in the current Project's tab group (supporting `key`, `title`, `preview`, and JSON `params` up to 64 KiB); `openFile(uri, options?)` opens a cloud file tab; file viewers use `setUnsavedChanges(unsaved)` to mark dirty tabs and `setFileHeaderItems(items)` to populate the file header **More** menu.
- **Files and document sessions (`fs`, `documents`)**: `fs.readTextDocument`, `fs.writeTextDocument`, and `fs.listTextEncodings` detect and convert text encodings in the main process; `fs.writeFile` accepts `expectedSha256` for optimistic concurrency; `documents.openText` and `documents.openBinary` manage `Cmd/Ctrl+S` saves, save-status indicators, and on-disk conflict prompts.
- **Floating Chat and traffic observation (`chat`)**: with `chat.composer`, views can call `chat.createFloatingChat({ context })` to mount the dockable floating chat button and update document context with `setContext`, or insert text with `chat.composer.insertText`. With `chat.observe`, extensions subscribe to chat traffic on the active Server connection via `chat.observe(filter, listener)` (up to 16 subscriptions per extension).
- **Execution sessions (`execution`)**: with `execution`, `execution.createSession` opens an execution session. The Server tracks conversation creators, allowing an extension to write only conversations it created, open host conversations without an accepted turn, and new conversations; saved conversations owned by another party return `readOnly: true`.
- **UI, dialogs, and themes (`ui`, `dialog`, `theme`)**: `ui.openExternal` opens approved hosts directly and prompts for others; `ui.previewImage` opens the host image preview overlay; `dialog.showMessageBox` opens a host confirmation dialog; `theme.apply()` and `theme.onDidChange` synchronize the active appearance mode, tokens, and CSS theme.

### Theme extensions (JSON and CSS)

- **JSON themes**: map allowed theme tokens under `common`, `light`, and `dark` (colors, `--radius`, font families); the host writes them to `<html>` and pushes public tokens to extension pages.
- **CSS themes**: standard CSS with package-local `@import` and inlined package images and fonts. The main process validates and inlines resources with PostCSS and `css-tree`, rejecting external URLs, paths leaving the package, and SVG scripts. Scope rules with `data-agw-document="desktop"` for the host or `data-agw-document="extension"` and `data-agw-extension="<id>"` for extension pages.

### Permissions and isolation

Declare all required permissions under `permissions` in `manifest.json`; Desktop displays them in a main-process confirmation window when enabling an extension:

- Standard permissions: `context.user`, `context.project`, `context.agent`, `fs.cloud.read`, `chat.composer`, `clipboard.write`.
- High-risk permissions (highlighted in the approval dialog): `project.repositories`, `fs.local.read`, `fs.local.write`, `fs.cloud.write`, `execution`, `a2a`, `chat.observe`, `clipboard.read`.

Network access is restricted to approved `hostPermissions` via a per-extension CSP and loopback egress proxy. Unapproved hosts, navigation, popups, and Service Workers are blocked.

## Combined Server/Desktop extensions

Combined extensions use `manifestVersion: 2` and `targets: 3`, declaring both `server` and `desktop` sections in one `manifest.json`:

- Place every Desktop asset under `desktop/` and reference that directory from `desktop.contributes`.
- Include SHA-256 digests for all Server and Desktop files under root `files`.
- Once enabled on Server, Desktop downloads the extension's Desktop bundle directly from the connected Server. The download preserves the original `manifest.json` and full `desktop/` asset set; `releaseHash` identifies the original manifest, and `packageHash` verifies the ZIP archive.

## Build, debug, and test

The repository includes complete reference extensions under `src/extensions/`:

- Combined extensions: `src/extensions/taskpilot`, `src/extensions/feishu`
- Desktop file viewers and views: `src/extensions/markdown`, `src/extensions/excalidraw`, `src/extensions/image-viewer`, `src/extensions/bare-repository`
- Theme extensions: `src/extensions/soft-theme`, `src/extensions/warm-minimalism-theme`

Build workspace extensions from `src`:

```bash
pnpm --filter @agw/extension-taskpilot build
pnpm --filter @agw/extension-feishu build
```

For local Desktop debugging, install the built output folder from **Settings → Extensions**, click **Reload** after rebuilding, use **Inspect** in the extension details panel to open DevTools for running frames, and inspect recently blocked requests.

Run extension tests from `src` (`test:extensions` launches Electron against an isolated SQLite Standalone Host):

```bash
pnpm --filter @agw/desktop test
pnpm --filter @agw/desktop test:extension-network
pnpm --filter @agw/desktop test:extensions
```

## Implementation and references

- [Server extension SDK](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Extensions.Sdk/README.md)
- [Server extension EF Core SDK](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Extensions.Sdk.EntityFrameworkCore/README.md)
- [Desktop extension SDK](https://github.com/zxyao145/agw/blob/main/src/clients/packages/extension-sdk/README.md)
- [Desktop extension runtime](https://github.com/zxyao145/agw/blob/main/src/clients/desktop/README.md)
- [TaskPilot extension](https://github.com/zxyao145/agw/blob/main/src/extensions/taskpilot/README.md)
- [Feishu extension](https://github.com/zxyao145/agw/blob/main/src/extensions/feishu/README.md)
