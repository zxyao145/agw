# Client Desktop Contracts Directory Reorganization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a real `@agw/desktop-contracts` workspace package and reorganize Desktop main/preload and Web Desktop feature code without changing runtime behavior.

**Architecture:** `@agw/desktop` and `@agw/web` remain application packages and both consume a framework-free CommonJS contracts package. Electron main/preload implementations stay in Desktop, renderer composition stays in Web, and only duplicated types plus the execution-status priority rule move into the shared package.

**Tech Stack:** pnpm 11.7.0, Turborepo 2.10.5, TypeScript 5.9.3, Node test runner through tsx 4.21.0, Electron 43, Next.js 16.

## Global Constraints

- Work only in `/Users/ben/source/repos/agw/.worktrees/agw-desktop-v1` on `codex/agw-desktop-v1`.
- Preserve the user's existing staged index and unrelated changes; do not stage or commit.
- Keep Mobile outside the pnpm workspace.
- Do not create `@agw/ui`, `@agw/api`, `@agw/auth`, empty renderer folders, empty tools folders, or a root TypeScript configuration.
- Do not change IPC channel names, payload shapes, persisted settings, HTTP behavior, SignalR behavior, renderer routes, or package flavors.
- Keep existing direct application dependency versions unchanged.
- Use Node 24 for Electron Forge verification.

---

### Task 1: Create `@agw/desktop-contracts` and wire the workspace graph

**Files:**
- Create: `src/clients/packages/desktop-contracts/package.json`
- Create: `src/clients/packages/desktop-contracts/tsconfig.json`
- Create: `src/clients/packages/desktop-contracts/README.md`
- Create: `src/clients/packages/desktop-contracts/src/server-profile.ts`
- Create: `src/clients/packages/desktop-contracts/src/settings.ts`
- Create: `src/clients/packages/desktop-contracts/src/runtime.ts`
- Create: `src/clients/packages/desktop-contracts/src/bridge.ts`
- Create: `src/clients/packages/desktop-contracts/src/execution.ts`
- Create: `src/clients/packages/desktop-contracts/src/execution.test.ts`
- Create: `src/clients/packages/desktop-contracts/src/index.ts`
- Modify: `src/clients/pnpm-workspace.yaml`
- Modify: `src/clients/turbo.json`
- Modify: `src/clients/web/package.json`
- Modify: `src/clients/desktop/package.json`
- Modify: `src/clients/pnpm-lock.yaml`

**Interfaces:**
- Consumes: the existing duplicated shapes in Desktop `desktop-contract.ts`, `settings-store.ts`, `local-server-runtime.ts`, `execution-status.ts`, and Web `desktop-runtime-model.ts`, `desktop-runtime.tsx`, `execution-activity-store.ts`.
- Produces: `DesktopPlatform`, `ServerProfile`, `PackageFlavor`, `CloseBehavior`, `DesktopSettings`, `LocalServerRuntime`, `DesktopRuntimeState`, `UninstallRequest`, `UninstallResult`, `AgwDesktopBridge`, `ExecutionKeyParts`, `ExecutionStatus`, and `aggregateExecutionStatus(statuses): ExecutionStatus`.

- [ ] **Step 1: Scaffold the workspace package and write the failing execution rule test**

Create a private package whose runtime and declaration entry points are `dist/index.js` and `dist/index.d.ts`. Use these scripts and exact tool versions:

```json
{
  "name": "@agw/desktop-contracts",
  "version": "0.1.0",
  "private": true,
  "main": "dist/index.js",
  "types": "dist/index.d.ts",
  "exports": {
    ".": {
      "types": "./dist/index.d.ts",
      "require": "./dist/index.js",
      "default": "./dist/index.js"
    }
  },
  "files": ["dist"],
  "scripts": {
    "build": "tsc -p tsconfig.json",
    "lint": "oxlint ./src",
    "test": "tsx --test src/*.test.ts",
    "format": "oxfmt ./src *.json",
    "format:check": "oxfmt --check ./src *.json"
  },
  "devDependencies": {
    "@types/node": "25.5.0",
    "oxfmt": "0.41.0",
    "oxlint": "1.56.0",
    "tsx": "4.21.0",
    "typescript": "5.9.3"
  }
}
```

Create `src/execution.test.ts`:

```ts
import assert from "node:assert/strict";
import test from "node:test";

import { aggregateExecutionStatus } from "./execution";

test("aggregateExecutionStatus follows the shared project priority", () => {
  assert.equal(aggregateExecutionStatus(["idle", "running"]), "running");
  assert.equal(aggregateExecutionStatus(["running", "failed-unread"]), "failed-unread");
  assert.equal(
    aggregateExecutionStatus(["failed-unread", "waiting-approval"]),
    "waiting-approval",
  );
  assert.equal(aggregateExecutionStatus([]), "idle");
});
```

Add `packages/*` to `pnpm-workspace.yaml`, then run `pnpm install` from `src/clients` so the new package receives its declared test tools.

- [ ] **Step 2: Run the test and verify the package has no implementation yet**

Run from `src/clients/packages/desktop-contracts`:

```bash
pnpm test
```

Expected: FAIL because `./execution` does not exist.

- [ ] **Step 3: Implement the browser-safe contracts**

Use the platform union already accepted by the Web renderer:

```ts
// server-profile.ts
export type DesktopPlatform =
  | "aix"
  | "android"
  | "darwin"
  | "freebsd"
  | "haiku"
  | "linux"
  | "openbsd"
  | "sunos"
  | "win32"
  | "cygwin"
  | "netbsd";

export type ServerProfile = {
  id: string;
  kind: "local" | "remote";
  name: string;
  baseUrl: string;
  apiMajorVersion: 1;
  allowInsecureHttp: boolean;
};
```

```ts
// settings.ts
import type { ServerProfile } from "./server-profile";

export type PackageFlavor = "full" | "client";
export type CloseBehavior = "minimize-to-tray" | "quit-desktop";

export type DesktopSettings = {
  schemaVersion: 1;
  packageFlavor: PackageFlavor;
  closeBehavior: CloseBehavior;
  profiles: ServerProfile[];
  activeServerId: string;
  projectTabsByServer: Record<string, string[]>;
};
```

```ts
// runtime.ts
import type { DesktopPlatform } from "./server-profile";
import type { DesktopSettings, PackageFlavor } from "./settings";

export type LocalServerRuntime = {
  schemaVersion: 1;
  pid: number;
  baseUrl: string;
  port: number;
  serverVersion: string;
  apiMajorVersion: 1;
  startedAt: string;
};

export type DesktopRuntimeState = {
  isDesktop: true;
  platform: DesktopPlatform;
  packageFlavor: PackageFlavor;
  settings: DesktopSettings;
  activeToken: string | null;
  localServerRuntime: LocalServerRuntime | null;
};
```

```ts
// bridge.ts
import type { DesktopRuntimeState } from "./runtime";
import type { DesktopSettings } from "./settings";

export type UninstallRequest = { deleteServerData: boolean };
export type UninstallResult = { manualActionRequired: boolean; message: string };

export type AgwDesktopBridge = {
  getRuntimeState(): Promise<DesktopRuntimeState>;
  saveSettings(settings: DesktopSettings): Promise<DesktopRuntimeState>;
  saveToken(profileId: string, token: string): Promise<void>;
  deleteToken(profileId: string): Promise<void>;
  provisionLocalToken(): Promise<string>;
  openSetup(baseUrl: string): Promise<void>;
  setActiveTaskCount(count: number): Promise<void>;
  prepareUninstall(request: UninstallRequest): Promise<UninstallResult>;
  showWindow(): Promise<void>;
  quitDesktop(): Promise<void>;
};
```

```ts
// execution.ts
export type ExecutionKeyParts = {
  serverId: string;
  projectId: string;
  contextId: string;
};

export type ExecutionStatus =
  | "idle"
  | "running"
  | "waiting-approval"
  | "completed-unread"
  | "failed-unread"
  | "detached";

const STATUS_PRIORITY: Record<ExecutionStatus, number> = {
  idle: 0,
  "completed-unread": 1,
  detached: 2,
  running: 3,
  "failed-unread": 4,
  "waiting-approval": 5,
};

export function aggregateExecutionStatus(statuses: ExecutionStatus[]): ExecutionStatus {
  return statuses.reduce<ExecutionStatus>(
    (current, status) =>
      STATUS_PRIORITY[status] > STATUS_PRIORITY[current] ? status : current,
    "idle",
  );
}
```

Export each public module from `src/index.ts`. Configure `tsconfig.json` with target `ES2022`, module `CommonJS`, module resolution `Node`, `rootDir: "src"`, `outDir: "dist"`, declarations, strict mode, and test exclusion.

- [ ] **Step 4: Add consumer dependencies and Turbo ordering**

Add this dependency to both application manifests without changing any existing versions:

```json
"@agw/desktop-contracts": "workspace:*"
```

Update Turbo so consumer-executing tasks build workspace dependencies first:

```json
"test": { "dependsOn": ["^build"], "env": ["CI", "TZ"] },
"test:e2e": { "dependsOn": ["^build"], "cache": false, "env": ["CI", "BACKEND_API_BASE_URL", "NEXT_PUBLIC_API_BASE_URL", "NEXT_OUTPUT_MODE"] },
"dev": { "dependsOn": ["^build"], "cache": false, "persistent": true, "env": ["AGW_*", "BACKEND_API_BASE_URL", "NEXT_PUBLIC_API_BASE_URL", "NEXT_OUTPUT_MODE"] },
"dev:renderer": { "dependsOn": ["^build"], "cache": false, "persistent": true, "env": ["AGW_*"] }
```

Also add `"dependsOn": ["^build"]` to `prepare:renderer`, `prepare:resources`, `package`, and `make`, preserving their current cache and environment settings.

Run `pnpm install` from `src/clients` to update the unified lock with both consumer links.

- [ ] **Step 5: Install and verify the package in isolation**

Run from `src/clients`:

```bash
pnpm install --frozen-lockfile
pnpm exec turbo run test build lint format:check --filter=@agw/desktop-contracts
```

Expected: the execution test passes, `dist/index.js` and `dist/index.d.ts` exist, and the lock has a `packages/desktop-contracts` importer.

---

### Task 2: Reorganize Electron main/preload and consume the shared contracts

**Files:**
- Move: `src/clients/desktop/src/main.ts` → `src/clients/desktop/src/main/index.ts`
- Move: `src/clients/desktop/src/preload.ts` → `src/clients/desktop/src/preload/index.ts`
- Move: `src/clients/desktop/src/daemon/*` → `src/clients/desktop/src/main/daemon/*`
- Move: `src/clients/desktop/src/local-server-runtime*` → `src/clients/desktop/src/main/runtime/local-server-runtime*`
- Move: `src/clients/desktop/src/local-token*` → `src/clients/desktop/src/main/runtime/local-token*`
- Move: `src/clients/desktop/src/server-executable-path*` → `src/clients/desktop/src/main/runtime/server-executable-path*`
- Move: `src/clients/desktop/src/server-profiles*` → `src/clients/desktop/src/main/settings/server-profiles*`
- Move: `src/clients/desktop/src/settings-store*` → `src/clients/desktop/src/main/settings/settings-store*`
- Move: `src/clients/desktop/src/renderer-path*` → `src/clients/desktop/src/main/renderer-path*`
- Move: `src/clients/desktop/src/chat-route.test.ts` → `src/clients/desktop/src/main/chat-route.test.ts`
- Move: `src/clients/desktop/src/electron-squirrel-startup.d.ts` → `src/clients/desktop/src/types/electron-squirrel-startup.d.ts`
- Create: `src/clients/desktop/src/main/execution-key.ts`
- Create: `src/clients/desktop/src/main/execution-key.test.ts`
- Delete: `src/clients/desktop/src/desktop-contract.ts`
- Delete: `src/clients/desktop/src/execution-status.ts`
- Delete: `src/clients/desktop/src/execution-status.test.ts`
- Modify: `src/clients/desktop/package.json`
- Modify: affected relative imports in all moved Desktop files

**Interfaces:**
- Consumes: all exports from `@agw/desktop-contracts` and the existing Desktop implementation behavior.
- Produces: Electron entry `dist/main/index.js`, preload entry `dist/preload/index.js`, and the unchanged colon-delimited `getExecutionKey(parts)` behavior.

- [ ] **Step 1: Extend the workflow test for the new compiled entry**

Extend the manifest type and assertion in `dev-workflow.test.ts`:

```ts
interface PackageManifest {
  main: string;
  scripts: Record<string, string>;
}

assert.equal(packageManifest.main, "dist/main/index.js");
```

Run `pnpm --filter @agw/desktop test`. Expected: FAIL because the current main entry is `dist/main.js`.

- [ ] **Step 2: Move implementation and tests without changing their behavior**

Keep tests beside their moved implementation. Update imports according to the new folders. In particular:

```ts
// main/settings/settings-store.ts
import type { DesktopSettings, PackageFlavor } from "@agw/desktop-contracts";
import { DEFAULT_LOCAL_PROFILE, validateServerProfiles } from "./server-profiles";
```

`DEFAULT_LOCAL_PROFILE`, `normalizeServerUrl`, and `validateServerProfiles` remain Desktop implementation exports, while `ServerProfile` is imported from the shared package.

```ts
// main/runtime/local-server-runtime.ts
import type { LocalServerRuntime } from "@agw/desktop-contracts";
```

```ts
// main/execution-key.ts
import type { ExecutionKeyParts } from "@agw/desktop-contracts";

export function getExecutionKey(parts: ExecutionKeyParts): string {
  return `${parts.serverId}:${parts.projectId}:${parts.contextId}`;
}
```

The new execution-key test retains the existing colon-delimited assertion. Aggregation assertions live only in the shared package test.

- [ ] **Step 3: Update main and preload to the shared bridge**

Import runtime, settings, uninstall, and bridge types from `@agw/desktop-contracts`. Keep `SecretCodec` imported from the Desktop settings store.

From `dist/main/index.js`, resolve development resources two levels above and preload as a sibling compiled directory:

```ts
const desktopRoot = resolve(__dirname, "..", "..");

function rendererRoot(): string {
  return app.isPackaged
    ? join(process.resourcesPath, "renderer")
    : join(desktopRoot, "resources", "renderer");
}

function trayIconPath(): string {
  return app.isPackaged
    ? join(process.resourcesPath, "assets", "tray-icon.svg")
    : join(desktopRoot, "assets", "tray-icon.svg");
}

// BrowserWindow webPreferences
preload: resolve(__dirname, "..", "preload", "index.js")
```

Update the chat-route test URL to `new URL("./index.ts", import.meta.url)`.

- [ ] **Step 4: Update the manifest and verify Desktop**

Set:

```json
"main": "dist/main/index.js"
```

Run from `src/clients`:

```bash
pnpm exec turbo run test lint build format:check --filter=@agw/desktop
```

Expected: all moved Desktop tests pass and both `desktop/dist/main/index.js` and `desktop/dist/preload/index.js` exist.

---

### Task 3: Consolidate the Web Desktop feature and consume shared contracts

**Files:**
- Move: `src/clients/web/src/components/desktop/app-shell.tsx` → `src/clients/web/src/features/desktop/components/app-shell.tsx`
- Move: `src/clients/web/src/components/desktop/app-shell.test.ts` → `src/clients/web/src/features/desktop/components/app-shell.test.ts`
- Move: `src/clients/web/src/components/desktop/desktop-project-picker.tsx` → `src/clients/web/src/features/desktop/components/project-picker.tsx`
- Move: `src/clients/web/src/components/desktop/desktop-project-picker.test.ts` → `src/clients/web/src/features/desktop/components/project-picker.test.ts`
- Move: `src/clients/web/src/lib/desktop-runtime-model.ts` → `src/clients/web/src/features/desktop/runtime-model.ts`
- Move: `src/clients/web/src/lib/desktop-runtime-model.test.ts` → `src/clients/web/src/features/desktop/runtime-model.test.ts`
- Move: `src/clients/web/src/lib/desktop-runtime.tsx` → `src/clients/web/src/features/desktop/runtime-provider.tsx`
- Create: `src/clients/web/src/features/desktop/index.ts`
- Modify: `src/clients/web/src/lib/execution-activity-store.ts`
- Modify: every Web import currently targeting `@/lib/desktop-runtime`, `@/lib/desktop-runtime-model`, or `@/components/desktop/*`

**Interfaces:**
- Consumes: `@agw/desktop-contracts`, existing Web API runtime configuration, and existing Desktop React behavior.
- Produces: `@/features/desktop` exports for `AppShell`, `DesktopRuntimeProvider`, `useDesktopRuntime`, Web connection model functions, and shared Desktop types.

- [ ] **Step 1: Point the runtime model test at the future feature path**

Move the existing runtime-model test with the implementation and keep all existing assertions. Before moving the implementation, run:

```bash
node --test src/features/desktop/runtime-model.test.ts
```

Expected: FAIL because `runtime-model.ts` is not yet present in the feature folder.

- [ ] **Step 2: Move the Desktop feature and remove duplicate data shapes**

In `runtime-model.ts`, import these shared types instead of declaring them:

```ts
import type {
  DesktopRuntimeState,
  DesktopSettings,
  ServerProfile,
} from "@agw/desktop-contracts";

export type { DesktopRuntimeState, DesktopSettings, ServerProfile } from "@agw/desktop-contracts";
```

Keep `ServerInfo`, `DesktopConnectionStatus`, `getActiveServerProfile`, `getEffectiveActiveServerProfile`, and `classifyDesktopConnection` in Web.

In `runtime-provider.tsx`, import `AgwDesktopBridge`, `DesktopRuntimeState`, `DesktopSettings`, and `ServerProfile` from the shared package. Keep the `Window.agwDesktop` declaration in this Web file:

```ts
declare global {
  interface Window {
    agwDesktop?: AgwDesktopBridge;
  }
}
```

Move components and update project-picker imports to their new relative feature paths.

- [ ] **Step 3: Use the shared execution rule in the Web store**

Replace the duplicate status union and priority table with:

```ts
import {
  aggregateExecutionStatus,
  type ExecutionKeyParts,
  type ExecutionStatus,
} from "@agw/desktop-contracts";

export type ExecutionSessionKey = ExecutionKeyParts;
export type { ExecutionStatus } from "@agw/desktop-contracts";
```

Keep `getExecutionSessionKey` and all store behavior unchanged.

- [ ] **Step 4: Add the feature barrel and update all consumers**

`features/desktop/index.ts` exports the App shell, runtime provider/hook, connection-model functions, and shared types required by current callers. Update imports in:

```text
src/app/layout.tsx
src/app/(app)/layout.tsx
src/app/(app)/settings/page.tsx
src/components/auth-gate.tsx
src/components/chat/chat-route-boundary.tsx
src/components/message/chat.tsx
src/lib/execution-activity.tsx
```

Ensure no imports remain from the deleted paths.

- [ ] **Step 5: Verify the Web package**

Run from `src/clients`:

```bash
pnpm exec turbo run format:check lint build --filter=@agw/web
node --test src/features/desktop/runtime-model.test.ts src/features/desktop/components/*.test.ts src/lib/execution-activity-store.test.ts
```

Expected: build and focused tests pass; lint has no new errors beyond the eight pre-existing unused-import warnings.

---

### Task 4: Synchronize repository documentation with the implemented package boundary

**Files:**
- Modify: `AGENTS.md`
- Modify: `CLAUDE.md`
- Modify: `README.md`
- Modify: `docs/1.Development.md`
- Modify: `docs/2.Architecture.md`
- Modify: `src/clients/desktop/README.md`
- Preserve without rewriting: `src/clients/directory-structure.md`

**Interfaces:**
- Consumes: the implemented workspace member and final application directories.
- Produces: identical agent instruction files and developer commands that describe the real package graph.

- [ ] **Step 1: Update the client workspace map**

Document that the pnpm workspace now contains `@agw/web`, `@agw/desktop`, and `@agw/desktop-contracts`. Explain that Web remains the Electron renderer and both applications share bridge/runtime/settings/execution contracts.

Update Desktop source references from flat `src/main.ts` and `src/preload.ts` paths to `src/main/index.ts` and `src/preload/index.ts` wherever they appear.

- [ ] **Step 2: Keep repository instructions byte-identical**

Apply the same changes to `AGENTS.md` and `CLAUDE.md`, then run:

```bash
cmp AGENTS.md CLAUDE.md
```

Expected: exit 0.

- [ ] **Step 3: Check for stale package and source references**

Run:

```bash
rg -n 'src/clients/desktop/src/(main|preload)\.ts|@/lib/desktop-runtime|@/components/desktop|src/clients/(web|desktop)/pnpm-lock\.yaml' \
  AGENTS.md CLAUDE.md README.md docs src/clients/desktop/README.md .github/workflows \
  --glob '!docs/superpowers/**'
```

Expected: no live documentation or workflow references remain, excluding historical plan/spec files where appropriate.

---

### Task 5: Perform full workspace, renderer, package, and diff verification

**Files:**
- Verify only: all files changed by Tasks 1-4

**Interfaces:**
- Consumes: final workspace and package graph.
- Produces: evidence that the reorganization is behavior-preserving and releasable.

- [ ] **Step 1: Verify the final dependency graph and lock**

Run from `src/clients`:

```bash
pnpm install --frozen-lockfile
pnpm list --depth=-1 --recursive
pnpm exec turbo run build --dry=json
```

Expected: four importers (`.`, `desktop`, `web`, `packages/desktop-contracts`), both applications depend on `@agw/desktop-contracts`, and Turbo schedules the contracts build before consumer builds.

- [ ] **Step 2: Run aggregate checks and prove cache reuse**

Using Node 24:

```bash
pnpm format:check
pnpm lint
pnpm test
pnpm build
pnpm build
```

Expected: all commands succeed, all Desktop and contracts tests pass, Web has only the known eight warnings, and the second build reports full local Turbo cache hits with remote caching disabled.

- [ ] **Step 3: Verify renderer export and Electron packaging**

```bash
pnpm prepare:renderer
AGW_PACKAGE_FLAVOR=client pnpm package:desktop
```

Expected: `desktop/resources/renderer/index.html` exists and the packaged `.app` contains renderer assets, `package-flavor.json` with `client`, `app.asar`, and the required runtime dependencies.

- [ ] **Step 4: Smoke the development task graph**

Run `pnpm dev:web`, request `http://localhost:3000/desktop/chat`, and expect HTTP 200. Use `pnpm --silent dev:desktop --dry=json` to verify `AGW_RENDERER_URL=http://localhost:3000`, cache disabled, and `persistent: true`. Do not open a second Electron instance if the user's existing instance is running.

- [ ] **Step 5: Run final repository audits**

```bash
cmp AGENTS.md CLAUDE.md
ruby -e 'require "yaml"; ARGV.each { |f| YAML.load_file(f) }' \
  .github/workflows/web.yml .github/workflows/desktop-release.yml
git diff --check
git status --short
```

Expected: all audits pass, only intended changes remain, the user's staged index is preserved, and no commit exists.
