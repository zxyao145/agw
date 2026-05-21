# Expo Prebuild Migration Design

Date: 2026-05-21
Project: Agw mobile app

## Goal

Convert the mobile project from a React Native CLI brownfield setup into an Expo prebuild app. The shared React Native UI remains the product surface, while Expo owns the app runtime configuration and generated native project structure.

The migration intentionally abandons the previous hand-written SwiftUI/Kotlin host shells and the custom `NativeAgwConfigFile` TurboModule. Existing installed config files are ignored. Users will re-import or save local configuration into the new Expo storage path.

## Current Context

The mobile workspace currently has:

- `shared/` as the React Native project root.
- `ios/` as a SwiftUI host that embeds React Native.
- `android/` as a Kotlin React Native host.
- `shared/src/rn/App.tsx` as the React Native entry component.
- `shared/src/rn/config/config-store.ts` reading and writing through `NativeAgwConfigFile`.
- Native implementations of `NativeAgwConfigFile` under both `ios/Agw/NativeAgwConfigFile/` and `android/app/src/main/java/com/agw/nativeconfig/`.

## Selected Approach

Use Expo SDK 55 with the prebuild workflow.

Expo SDK 55 is the current stable Expo SDK in the official documentation. It targets React Native 0.83 and React 19.2. The app should use Expo CLI commands and generated native projects instead of React Native CLI brownfield host wiring.

The React Native app entry will use Expo's `registerRootComponent(App)`. App configuration will live in `shared/app.json` under the top-level `expo` object, including app identity, package names, and config plugins.

## Storage Design

Replace `NativeAgwConfigFile` with `expo-secure-store`.

The local config remains serialized through the existing `serializeConfig` and `parseConfigFileContent` helpers, but the persistence boundary becomes a single SecureStore key such as `agw.localConfig`.

Behavior:

- `readLocalConfig()` reads the SecureStore value and parses it.
- `writeLocalConfig(config)` serializes and saves the value to SecureStore.
- `deleteLocalConfig()` deletes the SecureStore key.
- Old file-backed config locations are not read.
- No compatibility migration is performed.

SecureStore is appropriate because the config includes an API key. The implementation should still handle native storage errors by throwing normal JavaScript errors to the existing UI flow.

## Native Project Design

Expo prebuild should generate the native iOS and Android projects from `shared/app.json` and installed dependencies.

The previous native shells are no longer the source of truth:

- SwiftUI page routing is removed from the main workflow.
- Android launch props from `MainActivity.kt` are replaced by app-level defaults in JavaScript.
- The app defaults to the `home` route when no native initial props are present.

The repo may keep generated `ios/` and `android/` directories if the project wants committed prebuild output, but future native customization should be expressed through Expo app config or config plugins where practical.

## Package And Tooling Changes

`shared/package.json` should:

- Add `expo` and `expo-secure-store`.
- Use Expo-compatible `react` and `react-native` versions.
- Replace React Native CLI scripts with Expo scripts:
  - `start`: `expo start`
  - `android`: `expo run:android`
  - `ios`: `expo run:ios`
  - `prebuild`: `expo prebuild`

Testing remains Jest plus TypeScript. Existing tests should be adjusted only where the storage implementation or app entry behavior changes.

## Alternatives Considered

### Existing native shells plus Expo modules

This would install Expo modules into the current brownfield app while preserving SwiftUI and Kotlin host code. It is lower risk for native behavior but keeps the custom shells and TurboModule that this migration is meant to abandon.

### New clean Expo app and copy code over

This produces the cleanest structure but creates a larger diff and more file movement. The current React Native app is small enough to migrate in place under `shared/`.

### Compatibility migration from old config files

This would preserve existing installed user config, but the requested behavior is to ignore old install files and completely abandon the previous native persistence form.

## Verification

Minimum verification after implementation:

- `npm test` from `shared/`
- `npm run typecheck` from `shared/`
- `npx expo config` from `shared/`
- `npx expo install --check` from `shared/`

If local native toolchains are available, also run:

- `npx expo prebuild --clean --no-install` from `shared/`
- Android build through `npx expo run:android` or Gradle
- iOS build through `npx expo run:ios` or Xcode on macOS

## Out Of Scope

- Migrating old file-backed config data.
- Adding Expo Router.
- Adding EAS Build or EAS Update configuration.
- Recreating the old SwiftUI multi-page native host experience.
- Adding new product UI.
