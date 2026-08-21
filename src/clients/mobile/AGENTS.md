# Mobile Repository Guidelines

This directory is the Expo Router root for `@agw/mobile`.

## Project Structure

- `app/`: Expo Router route files and route-group layouts only.
- `src/features/`: Mobile-owned authentication, servers, Chat, Files, History, and workspace behavior.
- `src/components/` and `src/theme/`: React Native UI primitives and the Figma-derived light theme.
- `tests/`: Jest Expo and React Native Testing Library tests.
- `assets/`: committed app icons and visual assets.
- `android/`, `ios/`, `.expo/`, and `dist/`: generated output; do not hand-edit or commit.

Mobile may consume `@agw/api`, `@agw/execution-core`, `@agw/chat-core`, and `@agw/projects-core`. It must not import Web/Desktop applications, Web UI barrels, DOM APIs, or source files through relative monorepo paths.

## Commands

Run commands from `src/clients/`:

```bash
pnpm install
pnpm dev:mobile
pnpm android:mobile
pnpm ios:mobile
pnpm --filter @agw/mobile native:generate
pnpm --filter @agw/mobile typecheck
pnpm --filter @agw/mobile test
pnpm --filter @agw/mobile build
pnpm --filter @agw/mobile exec expo install --check
pnpm --filter @agw/mobile exec expo-doctor
```

Use Expo app config and config plugins for native settings. Regenerate native projects with Expo CNG instead of editing them.

## Security

Store only Profile metadata in AsyncStorage. Store each API token in its own Expo SecureStore key. Never log, render, or place tokens in route parameters or query caches. HTTP profiles require an explicit warning confirmation before connection.

## Tests and Style

Use TypeScript, React function components, kebab-case filenames, 2-space indentation, Jest Expo, and React Native Testing Library. Cover Profile migration, authentication, execution reconnect/stop behavior, destructive file actions, and both platform configurations when changing their boundaries.
