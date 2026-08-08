# Agw Desktop

Agw Desktop is the cross-platform Electron application for Agw. It owns an independent Next.js React renderer while reusing business modules from the client workspace packages. Chat is the primary workspace; Projects and the remaining administration routes live in Settings.

The Electron entry points are `src/main/index.ts` and `src/preload/index.ts`; the Desktop-owned Next.js application lives in `renderer/`. Its Electron React adapter is `renderer/src/runtime/`, and cross-process contracts remain internal under `src/shared/contracts/`. Desktop and Web do not import, locate, build, or consume artifacts from each other. Both compose reusable business modules from root `../packages/`, and execution state belongs to `@agw/chat`.

## Runtime model

- The default profile connects to `http://127.0.0.1:30816`.
- A Full package installs the bundled self-contained `Agw.Host` as a current-user daemon. A Client package contains no Server.
- If port 30816 is occupied, Server chooses an available loopback port and publishes it to `~/agw/runtime/server.json`; Desktop validates that descriptor and follows the live process.
- Desktop uses named Bearer tokens. Local first-run setup is the Server-owned `/setup` Razor page in a sandboxed modal window; after setup, the main process provisions a unique token and encrypts it with the operating system credential store.
- Multiple remote profiles are supported. HTTPS is required unless the user explicitly accepts the HTTP warning for a profile.
- Packaged renderer files are served only inside Electron through `agw://app`. OAuth completion uses the separately registered external protocol `agw-desktop://oauth/complete`, which always opens the Desktop Integrations route.

Every `server + project + conversation` has an independent SignalR connection. Switching Project tabs detaches the visible subscriber without stopping the task. Project status priority is: waiting for approval, failed, running, completed, idle.

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
pnpm lint
pnpm test
pnpm fmt
pnpm fmt:check
```

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

Releases contain eight unsigned installers:

- Windows x64: Full and Client Squirrel Setup EXEs
- macOS x64 and arm64: Full and Client DMGs
- Ubuntu x64: Full and Client DEBs

Installer names use `Agw-{version}-{full|client}-{platform}-{arch}` with `-Setup.exe`, `.dmg`, or `.deb` as appropriate.

`.github/workflows/release.yml` builds the complete matrix on every push to `main`, stable `vX.Y.Z` tag, and manual run. Main and default manual runs keep the installers as temporary Actions artifacts. A stable tag publishes them on the matching GitHub Release; a manual run publishes only when `publish` is enabled and `release_tag` contains a stable `vX.Y.Z` value. V1 intentionally has no automatic updater, code signing, or notarization.

## Close and uninstall behavior

Closing the window minimizes to the tray by default; users can change it to quit Desktop. The Server daemon continues in either case. In-app uninstall preparation unregisters the daemon and asks whether to retain or delete `~/agw`. Direct operating-system uninstall preserves `~/agw` by default.
