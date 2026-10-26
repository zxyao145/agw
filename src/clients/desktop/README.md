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

Chat messages render Markdown with Tiptap: `renderer/src/runtime/runtime-provider.tsx` passes `TiptapMarkdown` from `@agw/chat/markdown-tiptap` to `MarkdownRendererProvider`. Web keeps the default react-markdown renderer.

## Extensions

Extensions add Ribbon items, sidebar and main views, file viewers, a settings page, chat composer/message/conversation actions, file-tree actions and themes. They are managed under **Settings → Extensions** (`?category=themes` lists only theme extensions): install from a folder or a `.zip`, enable (approve permissions), disable, Reload, Uninstall (optionally deleting data), Rescan, open the extensions or data folder, and save the SDK types for authoring. Details also lists running frames with **Inspect** (DevTools) and recently blocked requests.

### Package

An extension is a directory named after its `id` with `manifest.json` at the root. The manifest is validated by `extensionManifestSchema` in `src/shared/extension-manifest.ts`; the generated `manifest.schema.json` can be referenced through `$schema`. Every path in the manifest is a relative POSIX path inside the package, and `__agw/` is reserved. The `id` is a lowercase GUID v7. `host` names the creator of the conversations Agw itself creates, so the `name` (trimmed, in any letter case) cannot be `host`. An installed extension whose `id` or `name` breaks these rules shows as invalid and is not loaded.

Pages load the SDK from `agw-extension://<id>/__agw/sdk.js` (types: `__agw/sdk.d.ts`). A background script uses `defineBackground({ onInstalled, activate, deactivate, commands })`; a view uses `defineView({ onLoad, onShow, onHide, onUnload })`; `connect()` returns the API directly. Pages receive the `ExtensionApi` (`context`, `workspace`, `fs`, `dialog`, `documents`, `shell`, `execution`, `a2a`, `http`, `chat`, `ui`, `theme`). Any front-end stack that builds to static files works (plain JS, React, Vue, Blazor WebAssembly, WASM). The SDK source and its types are the workspace package `@agw/extension-sdk` (`packages/extension-sdk`); a bundler that marks it external with the output path `/__agw/sdk.js` uses the types at build time and the served SDK at run time.

A `contributes.fileViewers` entry (`{ id, title, icon, patterns, entry, priority? }`, `priority` is `default` or `option`) opens matching files in the Files view. `patterns` match the file name case-insensitively (`*` and `?`). Matching viewers appear as tabs to the right of the file name, followed by **Source** and a **More** menu whose top row names the viewer and its extension (file viewers have no host title bar); the selected viewer follows the user's default for the file type, then `priority` (`default` before `option`), then the extension id. Below that row, **Reload view** reloads the selected viewer, or with **Source** selected **Reload file** reads the file again; with **Source** selected the top row describes the viewer selected before the switch. The viewer receives `{ file: { uri, name, projectId } }` in `onLoad`, and `onShow` again when the selected file changes or the hidden viewer is shown again; the host grants read access to that file. `workspace.openFile(uri)` opens another file of the same Project, and `workspace.setUnsavedChanges(unsaved)` marks the tab with "●": while a viewer has unsaved changes, selecting another file or directory, switching Project, opening Settings, reloading the page or quitting Desktop ask for confirmation first. `workspace.setFileHeaderItems(items)` (file viewers only) replaces the viewer's items in the file header: each `{ id, placement, label, disabled?, checked?, menu? }` item goes into the **More** menu, after **Reload view** (`placement: "top"`) or at the end after the default-viewer entries (`placement: "bottom"`); `disabled` greys an entry out, `checked` makes it a checkbox entry, and `menu` lists entries of the same shape that may hold one more submenu level (at most 16 items and 500 entries). The items show only while their viewer is selected, are cleared when the page closes or reloads, and `workspace.onDidSelectFileHeaderItem` receives `{ itemId, entryId }` (`entryId` is null for an item without a menu). `ui.openExternal` opens approved hosts directly and asks the user for any other `http(s)` address. `ui.previewImage({ data, type, name })` (views only) shows an image over the Desktop window in the same preview as chat images: wheel and button zoom, drag to pan, **Copy image** / **Save image as…** in the context menu, and closing by clicking the backdrop or `Esc`. It accepts PNG, JPEG, GIF, WebP, AVIF, BMP and SVG up to 32 MiB with a 1–255 character name (the alt text and the default file name), resolves when the user closes the preview, and fails with `ERR_FRAME_GONE` when the calling frame closes; one preview shows at a time and a new one replaces it.

Text files go through `fs.readTextDocument` (detects the encoding, BOM and line ending, or reads as a given encoding), `fs.writeTextDocument` (writes the text back in a chosen encoding, BOM and line ending) and `fs.listTextEncodings` (the supported encodings); encoding detection and conversion run in the main process. `fs.writeFile` accepts `expectedSha256`, the SHA-256 of the content read earlier, with `overwrite: true`: when the file on disk has another hash the write fails with `ERR_FILE_CHANGED` and the file is unchanged. `dialog.showMessageBox` shows a message box in the confirmation window, titled with the extension name, and returns the index of the clicked button. `documents.openText` and `documents.openBinary` open a document session for an editor: given an adapter for the editor's content, the session reads the file, reports unsaved changes, saves with `Cmd/Ctrl+S` under the content hash precondition, asks Overwrite/Reload/Cancel on a conflict and refreshes the content on `onShow`. The session also reports its save status, which the host shows after the file name while that viewer is selected: **Saved**, **Unsaved changes**, **Saving…**, or **Changed on disk** with a **Reload** button that makes the viewer's sessions read the changed file again (after confirming to discard unsaved changes).

`chat.composer.insertText` inserts text into the chat composer (permission `chat.composer`). `chat.observe(filter, listener)` (permission `chat.observe`, shown as high risk in the approval dialog) subscribes to the chat traffic of every conversation on the current Server: the requests chat sends over HTTP and the execution hub, their outcomes, and every message the Server returns, pushed live or read from history pages. The filter selects events by kind, channel, origin and conversation scope, and messages also by source, role, message type and content type. An extension receives only events of the current connection (the same profile, address and token), and `SettingCommand` events reach it without `environmentVariables`. An extension holds at most 16 subscriptions (`ERR_TOO_MANY_SUBSCRIPTIONS`); a subscription ends with `dispose()`, or when its frame closes, its page is replaced or the extension is disabled. Traffic of sessions created with `execution.createSession` carries the origin `{ kind: "extension", extensionId }`.

`execution.createSession` (permission `execution`) opens a session whose `SettingCommand` carries the extension's `id`, written by the host. The Server records who created each conversation (`extensionId`: `host` or an extension `id`) and lets an extension write only conversations it created, open conversations (host-created, never cleared and without an accepted turn; the first extension that writes one becomes its creator) and new ones; the host can continue any conversation, including an extension's. `createSession` accepts only lowercase UUIDs as `projectId` and `conversationId`, because they go into request paths; other values fail with `ERR_INVALID_ARGUMENT` before any request. For an existing conversation `createSession` first reads its details with the extension's origin and returns `readOnly`: true when the conversation is saved, not open and created by another party. On a read-only session `send`, `interrupt`, `respond` and `setPermissionMode` fail with `ERR_CONVERSATION_READ_ONLY`, also when the state changed after creation. A session's `onMessage` also receives the conversation's live messages this Desktop gets from other parties (the host's chat and execution drawers, and other extensions' sessions); turns running on other devices and history messages are not forwarded. Conversations from before this rule have the creator `host`: those without an accepted turn are open, the others are read-only for every extension.

A `contributes.themes` entry (`{ id, title, path }`) points to a package JSON or CSS file. A JSON theme `{ common?, light?, dark? }` maps theme variables to values; `light` and `dark` override `common`, and at least one of them is not empty. Only the variables listed in `src/shared/theme-tokens.ts` are allowed, and each value must be a valid CSS color, length (`--radius`) or `font-family` (`--agw-font-sans`, `--agw-font-mono`). **Settings → Appearance** chooses the mode (System, Light, Dark) and the theme (Default or the themes of enabled, valid extensions), both saved in `settings.json` (`appearance.theme`, `appearance.mode`). The host writes a JSON theme's variables on `<html>` and pushes the public variables in `theme.tokens`; using them is optional, and an extension page that calls `theme.apply()` gets the same variables. When the selected theme becomes unavailable, Desktop returns to Default and says so. The main process validates theme files after the manifest; failures are `ExtensionValidationError`s at `contributes.themes[n].path` (or the JSON field) with the package file, line and column, and the extension shows as invalid. Installation, directory scans and `scripts/build-bundled-extensions.mjs` use the same theme loading, so an invalid bundled theme fails the build.

A CSS theme is standard CSS: any custom variables, component styles, Grid/Flex layout, `@media`, `@supports`, `@layer` and `@keyframes`, following the CSS cascade. Rules on `:root` apply to both modes, `:root:not(.dark)` to light and `:root.dark` to dark. `<html>` carries `data-agw-document="desktop"` in the host and `data-agw-document="extension"` plus `data-agw-extension="<extensionId>"` in extension pages, so `:root[data-agw-document="desktop"] .agw-main-workspace { … }` scopes a rule to the host; generic selectors such as `body` or `button` reach every document that applies the theme. The theme may `@import` package CSS files (relative to the importing file, or package-rooted with `/`) with media, `supports()` and named or anonymous `layer` conditions, and refer to package images (PNG, JPEG, GIF, WebP, AVIF, SVG, ICO), fonts in `@font-face` (WOFF, WOFF2, TTF, OTF), data URLs of those types and fragment references such as `url(#filter)`. The main process expands the imports with PostCSS and `postcss-import`, checks every value with `css-tree` and the complete result with the CSS tokenizer, inlines package images and fonts as data URLs (SVG images are checked and their own package resources inlined), and computes the `styleId` (the SHA-256 of the text). External addresses (HTTP(S), `//`, `file:`, `blob:`, other extensions), queries, paths leaving the package (also through symbolic links), dynamic image addresses such as `image-set(var(--x) 1x)`, unparsable values, SVG scripts, event attributes, DOCTYPEs and XHTML content are errors. `@namespace` URIs are names and need an entry without `@import`; imported files cannot start with `@layer` statements. Limits: 1 MiB per CSS file, 4 MiB per decoded resource, 8 MiB of distinct inputs and decoded data URLs, 256 files, import depth 32, 256 import expansions and 16 MiB of processed text (`THEME_SIZE_LIMIT_EXCEEDED`). Compiled themes are cached by their whole dependency graph (paths, real paths, file identity, size and nanosecond times; watched directories invalidate entries, and **Reload** re-reads everything) within 64 MiB; the extension list carries only `{ format: "css", styleId, byteLength }`, and the renderer fetches the selected theme's text with `getThemeStyle`.

The host applies a CSS theme as a constructed stylesheet after its own stylesheets and keeps other adopted stylesheets. `theme.apply()` in an extension page applies `/__agw/theme-defaults.css` (the root custom property rules extracted from the host CSS at build time, with their modes, conditions, layers and `@property` rules) before the theme stylesheet, writes only `--agw-font-size-base` inline, and reuses the page's stylesheets while the `styleId` stays the same; `theme.get()` and `theme.onDidChange` include `cssText`. While a CSS theme is active the host samples every mounted view slot each frame and moves, resizes, hides and shows the native `WebContentsView` with it (hidden for `display: none`, `visibility: hidden`, zero effective opacity or `content-visibility: hidden`). The native view is an axis-aligned rectangle above the host page: rounded corners, partial transparency, filters, `clip-path`, rotation, scaling and DOM stacking on the slot or its ancestors do not apply to it.

### Bundled extensions

`src/clients/packages/extensions/<name>` holds extensions that ship with Desktop, each directory named after the extension's manifest `name` in kebab-case (`@agw/extension-<name>`, depending only on `@agw/extension-sdk`): **Markdown** opens `*.md`/`*.markdown` files in three viewers: **Mixed** (a block editor that writes back only the blocks you changed), **Preview** (read-only rendering) and **Code** (Monaco, for any text file, with syntax highlighting and Prettier formatting; **Format**, the **Word wrap** checkbox and **Save** are in the host's **More** menu). Text files are read with their detected encoding (UTF-8, UTF-16, GB18030 and other legacy encodings, with or without BOM) and written back in the same encoding and line ending; **Reopen with Encoding…** and **Save with Encoding…** are in the encoding menu of Code's status bar; Mixed has no top bar: its encoding is a submenu of the host's **More** menu, which holds the **Reopen with Encoding** and **Save with Encoding** submenus, followed by **Save**. `Cmd/Ctrl+S` saves, and a file changed on disk since it was opened shows an Overwrite/Reload/Cancel prompt. Mermaid code blocks render as diagrams in Mixed and Preview; in Mixed the source shows while the cursor is in the block, and the block's **Diagram only** toggle hides it entirely (the block is then selected as a whole, like an image). Clicking a diagram in Preview or in the Diagram only view, or its corner button, previews it through `ui.previewImage`. **Excalidraw** opens, edits and saves `*.excalidraw` files (`Cmd/Ctrl+S`). **Image Viewer** shows PNG, APNG, JPEG, GIF, WebP, AVIF, BMP, ICO and SVG files fitted to the view, with zoom buttons, actual size, `Ctrl/Cmd` + wheel or pinch zoom at the pointer and drag to pan; its status bar shows the pixel and file sizes, and it reads the file again each time it is shown. **Soft Theme** provides the Soft theme. **Warm Minimalism Theme** provides the Warm Minimalism theme: warm off-white surfaces, charcoal text and primary buttons, coral focus and running states, and fresh green success colors, with a warm charcoal dark variant. It also sets `--agw-workspace-backdrop`, a gradient-only theme variable: when a theme sets it, Desktop draws the geometric backdrop on the Main Workspace with the Chat column and the file Source area on cards, and the Markdown extension's Mixed and Preview draw it in their own pages with the document on a card. Extensions read the current theme's name from `theme.id` and `theme.title`. `scripts/build-bundled-extensions.mjs` builds them, validates each `dist/` the way Desktop reads an extension directory (manifest, package paths, icons, JSON and CSS themes) plus its SDK imports, and copies it to `resources/extensions/<id>`; `dev`, `test:extensions` and packaging run it, and packaged apps read them from the application resources. Bundled extensions are enabled on first run with their declared permissions approved, follow Desktop upgrades (new declarations are approved too), can be disabled like any extension, and stay removed after **Uninstall** (`removedBundledExtensions` in `settings.json`). A user-installed extension with the same `id` replaces the bundled one.

### Runtime

- Installed packages live in `<userData>/extensions/packages/<id>` (opened by **Open extensions folder**) and bundled ones in the application resources (`resources/extensions/<id>`); extension data lives in `<userData>/extensions/data/<id>` and is exposed to the extension as `context.extension.storageUri`. Updates replace only the package directory, and an uninstall deletes the data only when asked. `data`, `packages` and `host` are not valid extension ids. Install and uninstall use a `journal.json` in `packages/.staging/` so an interrupted operation is completed or rolled back at the next start.
- Each extension runs in its own session (`persist:agw-extension-<id>`), and every frame (background, sidebar view, main view, settings) is a separate `WebContentsView` created by the main process. The renderer's `ExtensionHost` (`renderer/src/runtime/extensions/`) owns the lifecycle, layout and the per-frame bridge broker; the main process (`src/main/extensions/`) owns installation, sessions, frames, local files, file watching and external HTTP.
- Network access is limited to the approved `hostPermissions`: a per-extension CSP covers page requests, a per-extension loopback egress proxy covers WebSocket and WebRTC TCP connections, and UDP outside the proxy is disabled. Navigation, popups and Service Workers are blocked. `http.request` follows redirects only to approved hosts.
- Host menus, dialogs and toasts that cover an extension view temporarily replace the view with a snapshot, so the host UI always stays on top. The image preview (chat images and `ui.previewImage`) covers the whole window, so every visible extension view shows its snapshot while it is open.
- Confirmations (opening a link outside the approved hosts, discarding unsaved changes, extension message boxes) and the permission approvals for installing and enabling extensions show in a modal `BrowserWindow` owned by the main process (`src/main/confirmation/`): its own document, preload, session and fixed styles with system fonts, served by `agw-confirmation://app/` with a strict CSP, so themes never reach it. Text from extensions renders as text, while permission descriptions, action meanings and buttons come from the app. Answers are accepted only from that window's IPC with the one-time request id; install and enable approvals bind the package digest, version and permission set and are checked again before taking effect, and closing the window or a changed package counts as canceled.
- Activation, deactivation, view handshakes and heartbeats have timeouts. A hung or crashed extension is marked failed without affecting other extensions; a hung or crashed view shows an error panel with a reload action.
- If the host page crashes or stops responding while extensions are activating, Desktop reloads in safe mode with extensions paused. **Reload without extensions** in the tray does the same on demand; **Resume extensions** on the Extensions page continues.

### Tests

```bash
pnpm --filter @agw/desktop test                     # unit tests, including main, SDK and renderer extension code
pnpm --filter @agw/desktop test:extension-network   # Electron: egress proxy, CSP and WebRTC rules
pnpm --filter @agw/desktop test:extensions          # Electron + SQLite Standalone Host: full extension flow
```

`test:extensions` builds the renderer, main bundle, SDK, the bundled extensions and the fixtures under `test-fixtures/`, starts a Standalone Host and local test services, and keeps all files under `.scratch/extension-tests/`. Electron runs three phases on the same user data: **first** (the full flow), **restart** (state that must survive a restart, such as an uninstalled bundled extension) and **performance**, which measures Mixed and Code in Electron and asserts the targets: Mixed opens 158 KB documents within 1500 ms and 631 KB ones within 4000 ms, a keystroke in the 631 KB documents updates the DOM within 50 ms, saving them (from `Cmd/Ctrl+S` to "Saved") takes at most 1000 ms, and Code opens a 10 MB text file within 3000 ms. The measurements are written to `.scratch/extension-tests/performance.json`. Each bundled extension package also has its own `test`, `typecheck` and `lint` scripts.

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
