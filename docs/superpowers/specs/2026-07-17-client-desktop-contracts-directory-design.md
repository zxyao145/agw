# Client Desktop Contracts and Directory Reorganization Design

## Goal

Reorganize `src/clients` into an application-and-packages monorepo without changing Agw runtime behavior. The first shared package will contain only contracts and pure rules that are already duplicated between the Electron main/preload process and the Next.js renderer.

The Web application remains the Electron renderer. This design does not create a second renderer application under `desktop`.

## Scope

- Keep the applications at `src/clients/web` and `src/clients/desktop`.
- Add `src/clients/packages/desktop-contracts` as `@agw/desktop-contracts`.
- Make both `@agw/web` and `@agw/desktop` depend on the shared package through `workspace:*`.
- Reorganize Desktop main-process and preload code by responsibility.
- Consolidate Web-only Desktop integration code under `web/src/features/desktop`.
- Preserve HTTP, SignalR, Electron preload, persistence, package-flavor, and renderer behavior.
- Keep Mobile outside the pnpm workspace.

## Non-goals

- Do not create `@agw/ui`, `@agw/api`, `@agw/auth`, or configuration packages yet.
- Do not move generic Web components that have only one consumer.
- Do not introduce a separate React renderer inside `desktop`.
- Do not redesign settings, execution state, server profiles, or IPC methods.
- Do not upgrade application dependencies.
- Do not create a Git commit as part of this work.

## Target Structure

```text
src/clients/
├── web/
│   ├── src/
│   │   ├── app/
│   │   ├── features/
│   │   │   └── desktop/
│   │   │       ├── components/
│   │   │       │   ├── app-shell.tsx
│   │   │       │   └── project-picker.tsx
│   │   │       ├── runtime-model.ts
│   │   │       ├── runtime-provider.tsx
│   │   │       └── index.ts
│   │   ├── api/
│   │   ├── components/
│   │   ├── hooks/
│   │   ├── lib/
│   │   └── types/
│   └── package.json
├── desktop/
│   ├── src/
│   │   ├── main/
│   │   │   ├── index.ts
│   │   │   ├── daemon/
│   │   │   ├── runtime/
│   │   │   │   ├── local-server-runtime.ts
│   │   │   │   ├── local-token.ts
│   │   │   │   └── server-executable-path.ts
│   │   │   ├── settings/
│   │   │   │   ├── server-profiles.ts
│   │   │   │   └── settings-store.ts
│   │   │   ├── execution-key.ts
│   │   │   └── renderer-path.ts
│   │   ├── preload/
│   │   │   └── index.ts
│   │   └── types/
│   │       └── electron-squirrel-startup.d.ts
│   ├── scripts/
│   ├── assets/
│   └── package.json
├── packages/
│   └── desktop-contracts/
│       ├── src/
│       │   ├── bridge.ts
│       │   ├── execution.ts
│       │   ├── runtime.ts
│       │   ├── server-profile.ts
│       │   ├── settings.ts
│       │   └── index.ts
│       ├── package.json
│       ├── tsconfig.json
│       └── README.md
├── package.json
├── pnpm-workspace.yaml
├── pnpm-lock.yaml
└── turbo.json
```

Workspace-wide scripts and a root TypeScript configuration are not added until they have a real consumer.

## Shared Package Boundary

`@agw/desktop-contracts` is framework-free and browser-safe. It must not depend on React, Next.js, Electron, Node.js APIs, storage, HTTP clients, or generated OpenAPI types.

### `server-profile.ts`

- `DesktopPlatform`
- `ServerProfile`

Platform-neutral types replace the current renderer copy and the Electron-only `NodeJS.Platform` reference at the bridge boundary. URL normalization and profile validation remain in the Desktop settings implementation because they are not currently used by both applications.

### `settings.ts`

- `PackageFlavor`
- `CloseBehavior`
- `DesktopSettings`

Encryption types, secret persistence, defaults, and file I/O remain in `desktop/src/main/settings/settings-store.ts`.

### `runtime.ts`

- `LocalServerRuntime`
- `DesktopRuntimeState`

The Web renderer stops maintaining an inline copy of the local server runtime shape.

### `bridge.ts`

- `UninstallRequest`
- `UninstallResult`
- `AgwDesktopBridge`

The preload implementation and the Web `Window.agwDesktop` declaration use the same bridge contract.

### `execution.ts`

- `ExecutionKeyParts`
- `ExecutionStatus`
- `aggregateExecutionStatus`

The shared package owns the status priority rule. The current Desktop and Web key serializers remain in their consumers because they produce different internal string formats; unifying those formats would be a separate behavioral change.

## Application Responsibilities

### Desktop

`desktop/src/main/index.ts` remains the Electron main entry point. Main-process implementations are grouped into daemon, runtime, and settings folders. `desktop/src/preload/index.ts` exposes the shared `AgwDesktopBridge` through Electron context isolation.

Moving the entry points requires updating `desktop/package.json`, preload path resolution, tests, and compiled output references from `dist/main.js` and `dist/preload.js` to their new paths.

### Web

Next.js routes stay in `web/src/app`. Desktop-specific renderer components, provider composition, and connection classification move into `web/src/features/desktop`. They import shared data shapes from `@agw/desktop-contracts` while keeping React state, API configuration, probing, and browser globals in the Web package.

The existing Web execution activity store imports the shared execution types and aggregation rule but retains its store and key serialization behavior.

## Dependency and Build Graph

```text
@agw/desktop ─┐
              ├──> @agw/desktop-contracts
@agw/web ─────┘
```

The workspace pattern becomes:

```yaml
packages:
  - web
  - desktop
  - packages/*
```

`@agw/desktop-contracts` compiles TypeScript to CommonJS JavaScript plus declarations in `dist`. CommonJS keeps the runtime package compatible with the current Electron compilation target, and Next.js can bundle the same output.

Turbo tasks that execute consumer code must build workspace dependencies first. `build`, tests, development startup, renderer preparation, and Desktop package/make therefore include the appropriate `^build` dependency. Side-effecting tasks remain uncached.

## Data Flow

1. Electron main loads and validates settings using Desktop implementations and shared contract types.
2. Electron main constructs `DesktopRuntimeState`.
3. Preload exposes an `AgwDesktopBridge` with the shared method signatures.
4. The Web Desktop runtime provider reads the bridge and configures REST and SignalR clients.
5. Desktop and Web execution indicators use the same `ExecutionStatus` and aggregation priority.

No new serialization boundary is introduced. Existing persisted JSON and IPC payload shapes remain unchanged.

## Error Handling

- Shared contracts contain no I/O and introduce no new error translation layer.
- Server URL and settings validation continue to throw the existing Desktop errors.
- Web connection probing and status classification keep their existing behavior.
- Electron IPC handlers continue to own platform and process errors.
- Package build failures surface through Turbo dependency ordering before either application starts or packages.

## Testing and Verification

- Add focused tests in `@agw/desktop-contracts` for execution status aggregation and exported contract shapes where runtime assertions are meaningful.
- Move existing Desktop tests with their implementations and keep all 21 current tests passing.
- Update Web tests and imports after moving Desktop feature files.
- Verify no duplicate `DesktopSettings`, `DesktopRuntimeState`, `ServerProfile`, `AgwDesktopBridge`, or `ExecutionStatus` definitions remain in the two applications.
- Run a frozen workspace install and confirm the lock contains `.`, `desktop`, `web`, and `packages/desktop-contracts` importers.
- Run root `format:check`, `lint`, `test`, and `build` twice; the second build must hit the local Turbo cache.
- Run `prepare:renderer` and a client-flavor Desktop package under Node 24.
- Smoke `dev:web` and validate `/desktop/chat`; dry-run `dev:desktop` when an existing user Electron instance prevents a safe second launch.
- Parse both GitHub Actions workflows, compare `AGENTS.md` and `CLAUDE.md`, and run `git diff --check`.

## Success Criteria

- Both applications import a real `@agw/desktop-contracts` workspace package.
- Shared bridge, runtime, settings, server profile, and execution status definitions have one source of truth.
- Desktop main/preload and Web Desktop feature directories match the target structure.
- The Electron renderer remains `@agw/web`; no unused renderer or UI package is created.
- Runtime interfaces, persistence formats, and application behavior remain unchanged.
- Workspace development, build, renderer export, and Desktop package flows pass from `src/clients`.
