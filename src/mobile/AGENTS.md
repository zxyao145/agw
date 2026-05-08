# Repository Guidelines

## Project Structure & Module Organization

This workspace contains native shells plus a shared React Native app.

- `shared/`: React Native project, Metro config, TypeScript source, and tests.
- `shared/src/rn/`: app entry and route definitions (`App.tsx`, `routes.ts`).
- `shared/__tests__/`: Jest tests for RN behavior and repository layout.
- `ios/`: SwiftUI host app. Open `ios/Agw.xcworkspace`; key files live in `ios/Agw/`.
- `android/`: Android/Kotlin host app. Key native files live in `android/app/src/main/java/com/agw/`.
- Assets live in `ios/Agw/Assets.xcassets` and `android/app/src/main/res/`.

Treat `shared/node_modules/`, `ios/Pods/`, Android build outputs, and Xcode derived data as generated.

## Build, Test, and Development Commands

Run React Native commands from `shared/`:

- `npm ci`: install from `package-lock.json`.
- `npm start`: start Metro on port `8081`.
- `npm run ios`: build and run iOS via React Native CLI.
- `npm run adb`: reverse Android port `8081` to Metro.
- `npm run android`: build, install, and launch Android debug.
- `npm test`: run Jest tests.
- `npm run typecheck`: run TypeScript checking with `tsc --noEmit`.

For iOS dependencies, run `bundle install` and `bundle exec pod install` from `ios/`. For Android-only builds, run `./gradlew :app:assembleDebug` or `./gradlew :app:installDebug` from `android/`.

## Coding Style & Naming Conventions

Use TypeScript and React function components for shared RN code. Keep filenames descriptive and consistent with current casing, such as `App.tsx` and `routes.ts`. Follow existing indentation: 2 spaces in TS/JS and 4 spaces in Swift; match nearby style for Kotlin and Gradle. Prefer typed route props in `shared/src/rn/routes.ts` when adding screens. Use Prettier-compatible RN formatting; `npm run lint` exists but needs an ESLint config before it is reliable.

## Testing Guidelines

Jest is the active test framework. Place RN tests in `shared/__tests__/` and name files `*.test.ts`, `*.test.tsx`, or `*.test.js`. Cover route registration, native initial props, and platform-specific rendering when changing navigation. Run `npm test` and `npm run typecheck` before submitting shared-code changes.

## Commit & Pull Request Guidelines

Git history uses Conventional Commits, for example `feat: init mobile template`, `fix(shared): ...`, and `docs(readme): ...`. Keep commits scoped with `feat:`, `fix:`, `refactor:`, `docs:`, `test:`, or `chore:`.

Pull requests should include a summary, linked issue when available, test results, and screenshots or recordings for UI changes. Call out native dependency, Pod, Gradle, or lockfile changes.

## Security & Configuration Tips

Do not commit local secrets, signing files, simulator settings, or machine-local environment files. Prefer setup steps in `README.md` and update lockfiles with dependency changes.
