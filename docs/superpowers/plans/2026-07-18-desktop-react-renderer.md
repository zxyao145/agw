# Independent Desktop React Renderer Plan

## Goal

Make `@agw/web` and `@agw/desktop` independent applications that build their own Next.js outputs while reusing infrastructure and business modules from root `src/clients/packages/*`.

## Architecture

- Web owns its browser route shell under `web/src/app` and has no Electron knowledge.
- Desktop owns Electron main/preload plus a Next.js renderer under `desktop/renderer`.
- The Desktop renderer is part of `@agw/desktop`, not a separate workspace package.
- Electron bridge contracts remain an internal seam at `desktop/src/shared/contracts`.
- Shared business modules never depend on Desktop contracts or an Electron adapter.
- Desktop packaging copies `desktop/renderer/out` and never locates or builds Web.

## Implementation

- [x] Add boundary tests that reject Web/Desktop cross-imports and cross-build scripts.
- [x] Move Electron React adaptation from Web into `desktop/renderer/src/runtime`.
- [x] Add a Desktop-owned Next.js App Router shell that composes root business packages.
- [x] Remove Desktop conditionals, bridge types, and the `/desktop/chat` route from Web.
- [x] Move bridge contracts into `desktop/src/shared/contracts` and remove `@agw/desktop-contracts`.
- [x] Make Desktop build, develop, and package its own renderer.
- [x] Remove root cross-application renderer assembly.
- [x] Complete workspace build, lint, test, format, and package verification.
