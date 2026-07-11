# Repository Guidelines

This workspace contains the Agw Expo mobile app.

## Project Structure & Module Organization

- `shared/`: Expo app root, package metadata, Metro/Babel/TypeScript config, source, and tests.
- `shared/src/rn/`: React Native app entry and route definitions (`App.tsx`, `routes.ts`).
- `shared/src/rn/config/`: local configuration parsing and Expo SecureStore persistence.
- `shared/__tests__/`: Jest tests for React Native behavior and repository layout.
- `shared/android/` and `shared/ios/`: Expo prebuild output when generated. Treat these as generated unless a task explicitly targets native output.
- `docs/`: project and workflow documentation.

The old top-level `ios/` SwiftUI shell and `android/` Kotlin shell are not part of the Expo workflow.

## Build, Test, and Development Commands

Run mobile commands from `shared/`:

- `npm ci`: install from `package-lock.json`.
- `npm start`: start Expo.
- `npm run android`: generate/build/run the Android development app through Expo.
- `npm run ios`: generate/build/run the iOS development app through Expo.
- `npm run prebuild`: generate Expo native projects from app config.
- `npm test`: run Jest tests.
- `npm run typecheck`: run TypeScript checking with `tsc --noEmit`.
- `npx expo config`: inspect resolved Expo config.
- `npx expo install --check`: verify Expo SDK-compatible package versions.

## Coding Style & Naming Conventions

Use TypeScript and React function components for shared React Native code. Keep filenames descriptive and consistent with current casing, such as `App.tsx` and `routes.ts`. Follow existing indentation: 2 spaces in TS/JS. Prefer typed route props in `shared/src/rn/routes.ts` when adding screens.

## Testing Guidelines

Jest is the active test framework. Place React Native tests in `shared/__tests__/` and name files `*.test.ts`, `*.test.tsx`, or `*.test.js`. Cover route registration, configuration persistence, and platform-specific rendering when changing navigation or native-facing behavior. Run `npm test` and `npm run typecheck` before submitting shared-code changes.

## Expo And Native Configuration

Prefer Expo app config, config plugins, and Expo modules over hand-written native code. If native projects are needed, regenerate them from `shared/` with:

```sh
npm run prebuild
```

Do not edit generated `shared/android/` or `shared/ios/` output unless the task is explicitly about generated native output.

Local Agw configuration is stored through `expo-secure-store`. Do not reintroduce the old file-backed `NativeAgwConfigFile` TurboModule.

## Commit & Pull Request Guidelines

Git history uses Conventional Commits, for example `feat:`, `fix(shared):`, and `docs(readme):`. Keep commits scoped with `feat:`, `fix:`, `refactor:`, `docs:`, `test:`, or `chore:`.

Pull requests should include a summary, linked issue when available, test results, and screenshots or recordings for UI changes. Call out native dependency, prebuild output, or lockfile changes.

## Security & Configuration Tips

Do not commit local secrets, signing files, simulator settings, or machine-local environment files. Prefer setup steps in `README.md` and update lockfiles with dependency changes.
