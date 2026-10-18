# Agw Desktop

Agw Desktop is the cross-platform Electron application for Agw. It owns an independent Next.js React renderer while reusing business modules from the client workspace packages. Chat is the primary workspace; Projects and the remaining administration routes live in Settings.

The Electron entry points are `src/main/index.ts` and `src/preload/index.ts`; the Desktop-owned Next.js application lives in `renderer/`. Its Electron React adapter is `renderer/src/runtime/`, and cross-process contracts remain internal under `src/shared/contracts/`. Desktop and Web do not import, locate, build, or consume artifacts from each other. Both compose reusable business modules from root `../packages/`, and execution state belongs to `@agw/chat-runtime`; `@agw/chat` owns the shared DOM renderer.

## Runtime model

- The default profile connects to `http://127.0.0.1:30816`.
- A Full package installs the bundled self-contained `Agw.Standalone.Host` as a current-user daemon. A Client package contains no Server.
- Server data defaults to `~/Library/Application Support/Agw` on macOS, `~/.local/share/agw` on Linux, and `%LOCALAPPDATA%\Agw` on Windows. Logs default to the data directory's `logs/` folder.
- If port 30816 is occupied, Server chooses an available loopback port and publishes it to `<data directory>/runtime/server.json`; Desktop validates that descriptor and follows the live process.
- Desktop uses named Bearer tokens, either entered manually or issued after third-party OIDC login in the system browser. Local first-run setup is the Server-owned `/setup` Razor page in a sandboxed modal window; after setup, the main process provisions a unique token and encrypts it with the operating system credential store.
- Multiple remote profiles are supported. HTTPS is required unless the user explicitly accepts the HTTP warning for a profile.
- Each Server profile owns an isolated React Query cache. Changing its URL or Token retires that cache, and switching profiles aborts stale connection probes and queries so data from the previous Server cannot overwrite the active view.
- Packaged renderer files are served only inside Electron through `agw://app`. OAuth completion uses the separately registered external protocol `agw-desktop://oauth/complete`, which always opens the Desktop Integrations route.

Every `server + project + conversation` has an independent SignalR connection. Switching Project tabs detaches the visible subscriber without stopping the task. Project status priority is: waiting for approval, failed, running, completed, idle.

Opening Settings from Chat carries the active Project and optional conversation in a validated `returnTo` query value. Settings navigation preserves that value, so **Back to chat** returns to the same selection; an invalid external or non-Chat target falls back to `/desktop/chat/`.

## Workspace layout

`/desktop/chat` is a three-region layout: the Ribbon (built-in Conversations and Files items, then extension items), the Left Sidebar (one sidebar view at a time; clicking the active item hides the sidebar), and the Main Workspace (the built-in Chat and File views or an extension main view). `@agw/chat` provides the pieces through `ChatWorkspaceProvider`, `ChatConversationSidebar`, `ChatFilesSidebar`, `ChatConversationPane` and `ChatFilePane`; Web keeps using `ChatWorkspace`.

## Extensions

Extensions add Ribbon items, sidebar and main views, file viewers, a settings page, chat composer/message/conversation actions and file-tree actions. They are managed under **Settings → Extensions**: install from a folder or a `.zip`, enable (approve permissions), disable, Reload, Uninstall (optionally deleting data), Rescan, open the extensions or data folder, and save the SDK types for authoring. Details also lists running frames with **Inspect** (DevTools) and recently blocked requests.

### Package

An extension is a directory named after its `id` with `manifest.json` at the root. The manifest is validated by `extensionManifestSchema` in `src/shared/extension-manifest.ts`; the generated `manifest.schema.json` can be referenced through `$schema`. Every path in the manifest is a relative POSIX path inside the package, and `__agw/` is reserved.

Pages load the SDK from `agw-extension://<id>/__agw/sdk.js` (types: `__agw/sdk.d.ts`). A background script uses `defineBackground({ onInstalled, activate, deactivate, commands })`; a view uses `defineView({ onLoad, onShow, onHide, onUnload })`; `connect()` returns the API directly. Pages receive the `ExtensionApi` (`context`, `workspace`, `fs`, `dialog`, `shell`, `execution`, `a2a`, `http`, `chat`, `ui`, `theme`). Any front-end stack that builds to static files works (plain JS, React, Vue, Blazor WebAssembly, WASM). The SDK source and its types are the workspace package `@agw/extension-sdk` (`packages/extension-sdk`); a bundler that marks it external with the output path `/__agw/sdk.js` uses the types at build time and the served SDK at run time.

A `contributes.fileViewers` entry (`{ id, title, icon, patterns, entry }`) opens matching files in the Files view. `patterns` match the file name case-insensitively (`*` and `?`). Matching viewers appear as tabs to the right of the file name, followed by **Source** and, while a viewer is selected, **Reload view** and an Info button whose popup names the viewer and its extension (file viewers have no host title bar); the first viewer is selected. The viewer receives `{ file: { uri, name, projectId } }` in `onLoad`, and `onShow` again when the selected file changes; the host grants read access to that file. `workspace.openFile(uri)` opens another file of the same Project, and `workspace.setUnsavedChanges(unsaved)` marks the tab with "●": while a viewer has unsaved changes, selecting another file or directory, switching Project, opening Settings, reloading the page or quitting Desktop ask for confirmation first. `ui.openExternal` opens approved hosts directly and asks the user for any other `http(s)` address.

### Bundled extensions

`src/clients/packages/extensions/<id>` holds extensions that ship with Desktop (`@agw/extension-<id>`, depending only on `@agw/extension-sdk`): **Markdown Preview** renders `*.md`/`*.markdown` files, and **Excalidraw** opens, edits and saves `*.excalidraw` files (`Cmd/Ctrl+S`). `scripts/build-bundled-extensions.mjs` builds them, validates each `dist/` (manifest, package paths and SDK imports) and copies it to `resources/extensions/<id>`; `dev`, `test:extensions` and packaging run it, and packaged apps read them from the application resources. Bundled extensions are enabled on first run with their declared permissions approved, follow Desktop upgrades (new declarations are approved too), can be disabled like any extension, and stay removed after **Uninstall** (`removedBundledExtensions` in `settings.json`). A user-installed extension with the same `id` replaces the bundled one.

### Runtime

- Installed packages live in `<userData>/extensions/<id>` and bundled ones in the application resources (`resources/extensions/<id>`); extension data lives in `<userData>/extension-data/<id>` and is exposed to the extension as `context.extension.storageUri`. Install and uninstall use a `journal.json` in `.staging/` so an interrupted operation is completed or rolled back at the next start.
- Each extension runs in its own session (`persist:agw-extension-<id>`), and every frame (background, sidebar view, main view, settings) is a separate `WebContentsView` created by the main process. The renderer's `ExtensionHost` (`renderer/src/runtime/extensions/`) owns the lifecycle, layout and the per-frame bridge broker; the main process (`src/main/extensions/`) owns installation, sessions, frames, local files, file watching and external HTTP.
- Network access is limited to the approved `hostPermissions`: a per-extension CSP covers page requests, a per-extension loopback egress proxy covers WebSocket and WebRTC TCP connections, and UDP outside the proxy is disabled. Navigation, popups and Service Workers are blocked. `http.request` follows redirects only to approved hosts.
- Host menus, dialogs and toasts that cover an extension view temporarily replace the view with a snapshot, so the host UI always stays on top.
- Activation, deactivation, view handshakes and heartbeats have timeouts. A hung or crashed extension is marked failed without affecting other extensions; a hung or crashed view shows an error panel with a reload action.
- If the host page crashes or stops responding while extensions are activating, Desktop reloads in safe mode with extensions paused. **Reload without extensions** in the tray does the same on demand; **Resume extensions** on the Extensions page continues.

### Tests

```bash
pnpm --filter @agw/desktop test                     # unit tests, including main, SDK and renderer extension code
pnpm --filter @agw/desktop test:extension-network   # Electron: egress proxy, CSP and WebRTC rules
pnpm --filter @agw/desktop test:extensions          # Electron + SQLite Standalone Host: full extension flow
```

`test:extensions` builds the renderer, main bundle, SDK, the bundled extensions and the fixtures under `test-fixtures/`, starts a Standalone Host and local test services, and keeps all files under `.scratch/extension-tests/`. Each bundled extension package also has its own `test`, `typecheck` and `lint` scripts.

## Develop

Install all client workspace dependencies once from the pnpm Workspace root:

```bash
cd src/clients
pnpm install
```

Run Desktop and its Renderer together:

```bash
cd src/clients
pnpm dev:desktop
```

Common workspace-wide checks from `src/clients` are:

```bash
pnpm build
pnpm typecheck
pnpm lint
pnpm test
pnpm fmt
pnpm fmt:check
```

`pnpm --filter @agw/desktop build:main` bundles the Electron main and preload entry points with esbuild into `dist/`, so the packaged application carries no runtime `node_modules`.

For a Desktop-only check, use `pnpm exec turbo run <task> --filter=@agw/desktop`.

Shared business UI is not duplicated in Desktop. Web and Desktop each own a thin route shell and independently compose domain packages under `../packages/`.

## Packages

`AGW_PACKAGE_FLAVOR` selects `full` (default) or `client`. Both variants use the same application identity and are mutually exclusive upgrades. Release packaging requires Node.js 24, matching GitHub Actions. Run the cross-platform wrapper from `src/clients`; it clears stale maker output, injects the release version, validates the bundled Server, and collects one installer under `desktop/release-artifacts/`.

```bash
pnpm release:desktop -- --flavor full --arch x64 --version 0.1.0
pnpm release:desktop -- --flavor client --arch x64 --version 0.1.0
# On macOS, --arch arm64 is also supported.
```

Direct `pnpm make:desktop` remains available for development packaging and defaults to the version in `desktop/package.json`.

Releases contain nine unsigned artifacts:

- Windows x64: Full and Client Squirrel Setup EXEs, plus a Client portable ZIP
- macOS x64 and arm64: Full and Client DMGs
- Ubuntu x64: Full and Client DEBs

Artifact names use `Agw-Desktop-{version}-{full|client}-{platform}-{arch}` with `-Setup.exe`, `-Portable.zip`, `.dmg`, or `.deb` as appropriate.

`.github/workflows/build-desktop.yml` builds the complete matrix for relevant pull requests, pushes to `main`, and manual runs, retaining temporary Actions artifacts. `.github/workflows/release.yml` reuses that matrix for `vX.Y.Z` and supported prerelease tags, or for a manually supplied release tag, then publishes the artifacts on the matching GitHub Release.

Opening About checks GitHub's latest stable Release and offers the artifact matching the current Full/Client flavor, platform, architecture, and Windows installation shape. Downloads open in the default browser and remain user-installed; Desktop does not check in the background, download automatically, or install updates. Code signing and notarization are not currently configured.

## Close and uninstall behavior

Closing the window minimizes to the tray by default; users can change it to quit Desktop. The Server daemon continues in either case. In-app uninstall preparation unregisters the daemon and asks whether to retain or delete the system-specific Server data directory. Direct operating-system uninstall preserves Server data by default.


## Third-party Server login

In Servers, each saved Server profile loads the providers enabled by that Server. Choose a provider to complete sign-in in the system browser. The main process keeps the pending state and Desktop PKCE verifier, validates the fixed `agw-desktop://auth/complete` callback, and receives the Agw Token directly over HTTPS. The IdP client secret and upstream Tokens stay on Server.

A login expires after ten minutes; the Server handoff code lasts two minutes. Cancelled, stale, duplicate, or changed-profile callbacks do not overwrite stored credentials. The OS-encrypted token and its OIDC credential-source marker are saved together. A failed local save attempts to revoke the newly issued Token.

Signing out removes the local credential and revokes that Token on Server. If the Server cannot be reached, the UI reports that revocation was not confirmed. The profile remains in explicit-login mode after logout or expiry, so a local profile cannot silently become the administrator. Use the explicit local-administrator action or sign in again.

Changing a profile's URL invalidates its saved OIDC credential. Switching between unchanged profiles does not revoke their Tokens. Existing manual-token profiles and integration OAuth callbacks retain their behavior.

Desktop Chat uses `/desktop/chat`. The previous `/chat` route is no longer exported; update saved links when upgrading.
