# Agw Desktop

Agw Desktop is the cross-platform Electron application for Agw. It owns an independent Next.js React renderer while reusing business modules from the client workspace packages. Chat is the primary workspace; Projects and the remaining administration routes live in Settings.

The Electron entry points are `src/main/index.ts` and `src/preload/index.ts`; the Desktop-owned Next.js application lives in `renderer/`. Its Electron React adapter is `renderer/src/runtime/`, and cross-process contracts remain internal under `src/shared/contracts/`. Desktop and Web do not import, locate, build, or consume artifacts from each other. Both compose reusable business modules from root `../packages/`, and execution state belongs to `@agw/chat-runtime`; `@agw/chat` owns the shared DOM renderer.

## Runtime model

- The default profile connects to `http://127.0.0.1:30816`.
- A Full package installs the bundled self-contained `Agw.Standalone.Host` as a current-user daemon. A Client package contains no Server.
- If port 30816 is occupied, Server chooses an available loopback port and publishes it to `~/agw/runtime/server.json`; Desktop validates that descriptor and follows the live process.
- Desktop uses named Bearer tokens, either entered manually or issued after third-party OIDC login in the system browser. Local first-run setup is the Server-owned `/setup` Razor page in a sandboxed modal window; after setup, the main process provisions a unique token and encrypts it with the operating system credential store.
- Multiple remote profiles are supported. HTTPS is required unless the user explicitly accepts the HTTP warning for a profile.
- Each Server profile owns an isolated React Query cache. Changing its URL or Token retires that cache, and switching profiles aborts stale connection probes and queries so data from the previous Server cannot overwrite the active view.
- Packaged renderer files are served only inside Electron through `agw://app`. OAuth completion uses the separately registered external protocol `agw-desktop://oauth/complete`, which always opens the Desktop Integrations route.

Every `server + project + conversation` has an independent SignalR connection. Switching Project tabs detaches the visible subscriber without stopping the task. Project status priority is: waiting for approval, failed, running, completed, idle.

Opening Settings from Chat carries the active Project and optional conversation in a validated `returnTo` query value. Settings navigation preserves that value, so **Back to chat** returns to the same selection; an invalid external or non-Chat target falls back to `/desktop/chat/`.

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

Closing the window minimizes to the tray by default; users can change it to quit Desktop. The Server daemon continues in either case. In-app uninstall preparation unregisters the daemon and asks whether to retain or delete `~/agw`. Direct operating-system uninstall preserves `~/agw` by default.


## Third-party Server login

In Servers, each saved Server profile loads the providers enabled by that Server. Choose a provider to complete sign-in in the system browser. The main process keeps the pending state and Desktop PKCE verifier, validates the fixed `agw-desktop://auth/complete` callback, and receives the Agw Token directly over HTTPS. The IdP client secret and upstream Tokens stay on Server.

A login expires after ten minutes; the Server handoff code lasts two minutes. Cancelled, stale, duplicate, or changed-profile callbacks do not overwrite stored credentials. The OS-encrypted token and its OIDC credential-source marker are saved together. A failed local save attempts to revoke the newly issued Token.

Signing out removes the local credential and revokes that Token on Server. If the Server cannot be reached, the UI reports that revocation was not confirmed. The profile remains in explicit-login mode after logout or expiry, so a local profile cannot silently become the administrator. Use the explicit local-administrator action or sign in again.

Changing a profile's URL invalidates its saved OIDC credential. Switching between unchanged profiles does not revoke their Tokens. Existing manual-token profiles and integration OAuth callbacks retain their behavior.

Desktop Chat uses `/desktop/chat`. The previous `/chat` route is no longer exported; update saved links when upgrading.
