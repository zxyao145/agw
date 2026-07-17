# Agw Desktop

Agw Desktop is the cross-platform Electron application for Agw. It owns an independent Next.js React renderer while reusing business modules from the client workspace packages. Chat is the primary workspace; Projects and the remaining administration routes live in Settings.

The Electron entry points are `src/main/index.ts` and `src/preload/index.ts`; the Desktop-owned Next.js application lives in `renderer/`. Its Electron React adapter is `renderer/src/runtime/`, and cross-process contracts remain internal under `src/shared/contracts/`. Desktop and Web do not import, locate, build, or consume artifacts from each other. Both compose reusable business modules from root `../packages/`, and execution state belongs to `@agw/chat`.

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
pnpm format
pnpm format:check
```

For a Desktop-only check, use `pnpm exec turbo run <task> --filter=@agw/desktop`.

Shared business UI is not duplicated in Desktop. Web and Desktop each own a thin route shell and independently compose domain packages under `../packages/`.

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
