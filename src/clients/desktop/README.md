# Agw Desktop

Agw Desktop is the cross-platform Electron shell for the exported Agw Web client. Chat is the only primary workspace; Projects and the remaining administration routes live in Settings.

The Electron entry points are `src/main/index.ts` and `src/preload/index.ts`; the renderer remains the Next.js application in `../web`. Both applications consume bridge, runtime, settings, server-profile, and execution contracts from the workspace package `@agw/desktop-contracts` in `../packages/desktop-contracts`.

## Runtime model

- The default profile connects to `http://127.0.0.1:30815`.
- A Full package installs the bundled self-contained `Agw.Host` as a current-user daemon. A Client package contains no Server.
- If port 30815 is occupied, Server chooses an available loopback port and publishes it to `~/agw/runtime/server.json`; Desktop validates that descriptor and follows the live process.
- Desktop uses named Bearer tokens. Local first-run setup is the Server-owned `/setup` Razor page in a sandboxed modal window; after setup, the main process provisions a unique token and encrypts it with the operating system credential store.
- One optional remote profile is supported. HTTPS is required unless the user explicitly accepts the HTTP warning.

Every `server + project + conversation` has an independent SignalR connection. Switching Project tabs detaches the visible subscriber without stopping the task. Project status priority is: waiting for approval, failed, running, completed, idle.

## Develop

Install all client workspace dependencies once from the pnpm Workspace root:

```bash
cd src/clients
pnpm install
```

Run the Web renderer in terminal 1:

```bash
cd src/clients
pnpm dev:web
```

After Next.js is ready, run the Desktop shell in terminal 2:

```bash
cd src/clients
pnpm dev:desktop
```

Common workspace-wide checks from `src/clients` are:

```bash
pnpm build
pnpm lint
pnpm test
pnpm format
pnpm format:check
```

For a Desktop-only check, use `pnpm exec turbo run <task> --filter=@agw/desktop`.

## Packages

`AGW_PACKAGE_FLAVOR` selects `full` (default) or `client`. Both variants use the same application identity and are mutually exclusive upgrades.

```bash
AGW_PACKAGE_FLAVOR=client pnpm make:desktop
AGW_PACKAGE_FLAVOR=full pnpm make:desktop
pnpm make:desktop -- --arch=x64
```

Preview releases contain eight unsigned installers:

- Windows x64: Full and Client Squirrel Setup EXEs
- macOS x64 and arm64: Full and Client DMGs
- Ubuntu x64: Full and Client DEBs

On a Git tag matching `desktop-v*`, `.github/workflows/desktop-release.yml` builds those artifacts and publishes a prerelease. V1 intentionally has no automatic updater and no code signing.

## Close and uninstall behavior

Closing the window minimizes to the tray by default; users can change it to quit Desktop. The Server daemon continues in either case. In-app uninstall preparation unregisters the daemon and asks whether to retain or delete `~/agw`. Direct operating-system uninstall preserves `~/agw` by default.
