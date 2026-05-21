# Expo Prebuild Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Convert `src/mobile/shared` into an Expo SDK 55 prebuild app and remove the old hand-written native host workflow.

**Architecture:** `shared/` remains the JavaScript app root. Expo owns app config and native project generation through `app.json` plus `expo prebuild`; local Agw configuration is stored only in `expo-secure-store`. The old top-level `ios/` and `android/` shells are removed from the primary workflow.

**Tech Stack:** Expo SDK 55, React Native 0.83.6, React 19.2.0, TypeScript, Jest, `expo-secure-store`.

---

## File Structure

- Modify `shared/package.json`: replace React Native CLI dependencies and scripts with Expo SDK 55 dependencies and Expo CLI scripts.
- Modify `shared/app.json`: convert to a top-level `expo` config with app identity, package names, and the SecureStore config plugin.
- Modify `shared/index.js`: replace direct `AppRegistry` registration with Expo `registerRootComponent`.
- Modify `shared/babel.config.js`: use `babel-preset-expo`.
- Modify `shared/metro.config.js`: use `expo/metro-config`.
- Modify `shared/jest.config.js`: use `jest-expo`.
- Modify `shared/tsconfig.json`: extend `expo/tsconfig.base`.
- Delete `shared/react-native.config.js`: no React Native CLI sibling-project mapping remains.
- Modify `shared/src/rn/config/config-store.ts`: replace `NativeAgwConfigFile` with `expo-secure-store`.
- Delete `shared/src/rn/specs/NativeAgwConfigFile.ts`: the custom TurboModule boundary is removed.
- Create `shared/__tests__/config-store.test.ts`: cover the new SecureStore persistence boundary.
- Modify `shared/__tests__/projectLayout.test.js`: assert Expo project layout rather than SwiftUI/Kotlin host layout.
- Modify `.gitignore`: ignore Expo local state and generated `shared/android/` and `shared/ios/` directories.
- Modify `README.md` and `AGENTS.md`: document Expo commands and remove old native shell instructions.
- Delete top-level `ios/` and `android/`: remove the old generated/custom native shells from the main repo tree.

## Task 1: Expo Tooling And App Entry

**Files:**
- Modify: `shared/package.json`
- Modify: `shared/app.json`
- Modify: `shared/index.js`
- Modify: `shared/babel.config.js`
- Modify: `shared/metro.config.js`
- Modify: `shared/jest.config.js`
- Modify: `shared/tsconfig.json`
- Delete: `shared/react-native.config.js`

- [ ] **Step 1: Update `shared/package.json`**

Use this dependency and script shape:

```json
{
  "scripts": {
    "start": "expo start",
    "android": "expo run:android",
    "ios": "expo run:ios",
    "prebuild": "expo prebuild",
    "test": "jest",
    "typecheck": "tsc --noEmit"
  },
  "dependencies": {
    "expo": "~55.0.26",
    "expo-secure-store": "~55.0.14",
    "expo-system-ui": "~55.0.18",
    "react": "19.2.0",
    "react-native": "0.83.6",
    "react-native-safe-area-context": "~5.6.2"
  },
  "devDependencies": {
    "@babel/core": "^7.25.2",
    "@types/jest": "^29.5.13",
    "@types/react": "~19.2.2",
    "@types/react-test-renderer": "^19.1.0",
    "babel-preset-expo": "~55.0.22",
    "jest": "^29.6.3",
    "jest-expo": "~55.0.18",
    "react-test-renderer": "19.2.0",
    "typescript": "~5.9.2"
  }
}
```

Keep `name`, `version`, `private`, and `engines.node`.

- [ ] **Step 2: Replace `shared/app.json` with Expo config**

```json
{
  "expo": {
    "name": "Agw",
    "slug": "agw",
    "version": "0.0.1",
    "scheme": "agw",
    "orientation": "portrait",
    "userInterfaceStyle": "automatic",
    "ios": {
      "bundleIdentifier": "com.agw",
      "supportsTablet": true,
      "config": {
        "usesNonExemptEncryption": false
      }
    },
    "android": {
      "package": "com.agw"
    },
    "plugins": [
      [
        "expo-secure-store",
        {
          "configureAndroidBackup": true,
          "faceIDPermission": "Allow Agw to access Face ID for protected local configuration."
        }
      ]
    ]
  }
}
```

- [ ] **Step 3: Replace `shared/index.js`**

```js
/**
 * @format
 */

import { registerRootComponent } from 'expo';
import App from './src/rn/App';

registerRootComponent(App);
```

- [ ] **Step 4: Replace `shared/babel.config.js`**

```js
module.exports = {
  presets: ['babel-preset-expo'],
};
```

- [ ] **Step 5: Replace `shared/metro.config.js`**

```js
const { getDefaultConfig } = require('expo/metro-config');

module.exports = getDefaultConfig(__dirname);
```

- [ ] **Step 6: Replace `shared/jest.config.js`**

```js
module.exports = {
  preset: 'jest-expo',
};
```

- [ ] **Step 7: Replace `shared/tsconfig.json`**

```json
{
  "extends": "expo/tsconfig.base",
  "compilerOptions": {
    "types": ["jest"]
  },
  "include": ["**/*.ts", "**/*.tsx"],
  "exclude": ["**/node_modules", "**/Pods", "android", "ios"]
}
```

- [ ] **Step 8: Delete `shared/react-native.config.js`**

Remove the file because Expo prebuild no longer needs React Native CLI source-directory overrides.

- [ ] **Step 9: Commit the tooling boundary**

```bash
git add shared/package.json shared/app.json shared/index.js shared/babel.config.js shared/metro.config.js shared/jest.config.js shared/tsconfig.json shared/react-native.config.js
git commit -m "chore(mobile): switch shared app to expo tooling"
```

## Task 2: SecureStore Config Persistence

**Files:**
- Create: `shared/__tests__/config-store.test.ts`
- Modify: `shared/src/rn/config/config-store.ts`
- Delete: `shared/src/rn/specs/NativeAgwConfigFile.ts`

- [ ] **Step 1: Add the failing SecureStore persistence test**

Create `shared/__tests__/config-store.test.ts`:

```ts
import * as SecureStore from "expo-secure-store";
import { readLocalConfig, writeLocalConfig, deleteLocalConfig } from "../src/rn/config/config-store";

jest.mock("expo-secure-store", () => ({
  getItemAsync: jest.fn(),
  setItemAsync: jest.fn(),
  deleteItemAsync: jest.fn(),
}));

const getItemAsyncMock = SecureStore.getItemAsync as jest.MockedFunction<typeof SecureStore.getItemAsync>;
const setItemAsyncMock = SecureStore.setItemAsync as jest.MockedFunction<typeof SecureStore.setItemAsync>;
const deleteItemAsyncMock = SecureStore.deleteItemAsync as jest.MockedFunction<typeof SecureStore.deleteItemAsync>;

describe("config-store", () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it("reads Agw config from Expo SecureStore", async () => {
    getItemAsyncMock.mockResolvedValue(
      JSON.stringify({
        version: 1,
        serverDomain: "https://api.example.com/",
        apiKey: "stored-key",
      })
    );

    await expect(readLocalConfig()).resolves.toEqual({
      version: 1,
      serverDomain: "https://api.example.com",
      apiKey: "stored-key",
    });
    expect(getItemAsyncMock).toHaveBeenCalledWith("agw.localConfig");
  });

  it("returns null when no SecureStore config exists", async () => {
    getItemAsyncMock.mockResolvedValue(null);

    await expect(readLocalConfig()).resolves.toBeNull();
  });

  it("writes normalized Agw config to Expo SecureStore", async () => {
    await writeLocalConfig({
      version: 1,
      serverDomain: "https://api.example.com",
      apiKey: "stored-key",
    });

    expect(setItemAsyncMock).toHaveBeenCalledTimes(1);
    expect(setItemAsyncMock.mock.calls[0][0]).toBe("agw.localConfig");
    expect(JSON.parse(setItemAsyncMock.mock.calls[0][1])).toEqual({
      version: 1,
      serverDomain: "https://api.example.com",
      apiKey: "stored-key",
    });
  });

  it("deletes the Expo SecureStore config key", async () => {
    await deleteLocalConfig();

    expect(deleteItemAsyncMock).toHaveBeenCalledWith("agw.localConfig");
  });
});
```

- [ ] **Step 2: Run the targeted test and verify failure**

Run from `shared/`:

```bash
npm test -- --runTestsByPath __tests__/config-store.test.ts
```

Expected before implementation: failure caused by `expo-secure-store` not being imported by `config-store.ts` or the old native module path still being used.

- [ ] **Step 3: Replace `shared/src/rn/config/config-store.ts`**

```ts
import * as SecureStore from "expo-secure-store";
import type { AgwLocalConfig } from "./agw-config";
import { parseConfigFileContent, serializeConfig } from "./agw-config";

const localConfigKey = "agw.localConfig";

export async function readLocalConfig(): Promise<AgwLocalConfig | null> {
  const content = await SecureStore.getItemAsync(localConfigKey);

  if (!content) {
    return null;
  }

  return parseConfigFileContent(content);
}

export async function writeLocalConfig(config: AgwLocalConfig): Promise<void> {
  await SecureStore.setItemAsync(localConfigKey, serializeConfig(config));
}

export async function deleteLocalConfig(): Promise<void> {
  await SecureStore.deleteItemAsync(localConfigKey);
}
```

- [ ] **Step 4: Delete `shared/src/rn/specs/NativeAgwConfigFile.ts`**

Remove the obsolete TurboModule TypeScript spec.

- [ ] **Step 5: Run the targeted test and verify pass**

Run from `shared/`:

```bash
npm test -- --runTestsByPath __tests__/config-store.test.ts
```

Expected: all `config-store` tests pass.

- [ ] **Step 6: Commit the storage change**

```bash
git add shared/__tests__/config-store.test.ts shared/src/rn/config/config-store.ts shared/src/rn/specs/NativeAgwConfigFile.ts
git commit -m "refactor(mobile): store config with expo secure store"
```

## Task 3: Expo Layout Tests And Documentation

**Files:**
- Modify: `shared/__tests__/projectLayout.test.js`
- Modify: `.gitignore`
- Modify: `README.md`
- Modify: `AGENTS.md`

- [ ] **Step 1: Replace `shared/__tests__/projectLayout.test.js`**

```js
const fs = require('fs');
const path = require('path');

function findMobileRoot(startPath) {
  let current = startPath;

  while (current !== path.dirname(current)) {
    if (
      fs.existsSync(path.join(current, 'AGENTS.md')) &&
      fs.existsSync(path.join(current, 'shared', 'package.json'))
    ) {
      return current;
    }

    current = path.dirname(current);
  }

  throw new Error(`Could not find mobile repository root from ${startPath}`);
}

const repoRoot = findMobileRoot(__dirname);
const sharedRoot = path.join(repoRoot, 'shared');

describe('project layout', () => {
  it('uses shared as the Expo app root', () => {
    const appConfig = JSON.parse(fs.readFileSync(path.join(sharedRoot, 'app.json'), 'utf8'));
    const packageJson = JSON.parse(fs.readFileSync(path.join(sharedRoot, 'package.json'), 'utf8'));

    expect(appConfig.expo.name).toBe('Agw');
    expect(appConfig.expo.slug).toBe('agw');
    expect(appConfig.expo.ios.bundleIdentifier).toBe('com.agw');
    expect(appConfig.expo.android.package).toBe('com.agw');
    expect(packageJson.scripts.start).toBe('expo start');
    expect(packageJson.scripts.android).toBe('expo run:android');
    expect(packageJson.scripts.ios).toBe('expo run:ios');
    expect(packageJson.dependencies.expo).toMatch(/^~55\./);
    expect(packageJson.dependencies['expo-secure-store']).toMatch(/^~55\./);
  });

  it('registers the app through Expo instead of React Native CLI metadata', () => {
    const index = fs.readFileSync(path.join(sharedRoot, 'index.js'), 'utf8');

    expect(index).toContain("import { registerRootComponent } from 'expo'");
    expect(index).toContain('registerRootComponent(App)');
    expect(fs.existsSync(path.join(sharedRoot, 'react-native.config.js'))).toBe(false);
  });

  it('removes the old top-level native host projects from the source tree', () => {
    expect(fs.existsSync(path.join(repoRoot, 'ios', 'Agw', 'ReactViewController.swift'))).toBe(false);
    expect(fs.existsSync(path.join(repoRoot, 'android', 'app', 'src', 'main', 'java', 'com', 'agw', 'MainActivity.kt'))).toBe(false);
  });
});
```

- [ ] **Step 2: Update `.gitignore`**

Add these lines near the React Native or Expo ignore section:

```gitignore
shared/.expo/
shared/android/
shared/ios/
```

- [ ] **Step 3: Rewrite `README.md` for Expo workflow**

The README should state that the app root is `shared/` and list these commands:

```sh
cd shared
npm ci
npm start
npm run android
npm run ios
npm run prebuild
npm test
npm run typecheck
```

It should also state that local config now uses `expo-secure-store`, old native config files are ignored, and generated `shared/android/` plus `shared/ios/` should be regenerated with Expo prebuild.

- [ ] **Step 4: Rewrite mobile `AGENTS.md` for Expo workflow**

The file should describe:

- `shared/` as the Expo app root.
- `shared/src/rn/` as the React Native source location.
- `shared/__tests__/` as the Jest test location.
- `shared/android/` and `shared/ios/` as generated Expo prebuild output when present.
- Commands from `shared/`: `npm ci`, `npm start`, `npm run android`, `npm run ios`, `npm run prebuild`, `npm test`, `npm run typecheck`.
- No old SwiftUI/Kotlin host shell workflow.

- [ ] **Step 5: Commit tests and docs**

```bash
git add shared/__tests__/projectLayout.test.js .gitignore README.md AGENTS.md
git commit -m "docs(mobile): document expo prebuild workflow"
```

## Task 4: Remove Old Native Shells

**Files:**
- Delete: `ios/`
- Delete: `android/`

- [ ] **Step 1: Remove top-level native shell directories**

Run from `src/mobile`:

```bash
git rm -r ios android
```

Expected: all old SwiftUI, Xcode, Kotlin, Gradle, and native TurboModule files are staged for removal.

- [ ] **Step 2: Run the layout test**

Run from `shared/`:

```bash
npm test -- --runTestsByPath __tests__/projectLayout.test.js
```

Expected: the Expo layout tests pass and no test expects top-level `ios/` or `android/` host projects.

- [ ] **Step 3: Commit native shell removal**

```bash
git add -u ios android
git commit -m "refactor(mobile): remove legacy native shells"
```

## Task 5: Dependency Install And Verification

**Files:**
- Modify: `shared/package-lock.json`

- [ ] **Step 1: Install Expo dependencies and update lockfile**

Run from `shared/`:

```bash
npm install --registry=https://registry.npmjs.org/
```

Expected: `package-lock.json` updates to Expo SDK 55 packages and no install error occurs.

- [ ] **Step 2: Run Jest**

Run from `shared/`:

```bash
npm test
```

Expected: all Jest suites pass.

- [ ] **Step 3: Run TypeScript**

Run from `shared/`:

```bash
npm run typecheck
```

Expected: TypeScript exits with code 0.

- [ ] **Step 4: Validate Expo config**

Run from `shared/`:

```bash
npx expo config
```

Expected: Expo prints resolved config with `name: Agw`, `slug: agw`, iOS bundle identifier `com.agw`, and Android package `com.agw`.

- [ ] **Step 5: Validate Expo package compatibility**

Run from `shared/`:

```bash
npx expo install --check
```

Expected: Expo reports dependencies compatible with SDK 55, or prints exact package fixes to apply. Apply any exact fixes with `npx expo install --fix`, then repeat the check.

- [ ] **Step 6: Verify Android prebuild generation**

Run from `shared/`:

```bash
$env:EXPO_NO_GIT_STATUS = "1"
npx expo prebuild --platform android --clean --no-install
```

Expected on Windows: Expo generates `shared/android/` without modifying committed source files outside ignored generated output.

- [ ] **Step 7: Commit lockfile**

```bash
git add shared/package-lock.json
git commit -m "chore(mobile): update expo dependency lockfile"
```

## Self-Review

Spec coverage:

- Expo SDK 55 prebuild target is covered by Tasks 1, 3, and 5.
- SecureStore replacement with no old-file migration is covered by Task 2.
- Old SwiftUI/Kotlin shell removal is covered by Tasks 3 and 4.
- Verification commands from the design are covered by Task 5.

Marker scan:

- No unresolved markers are present.

Type consistency:

- The SecureStore key is consistently named `agw.localConfig`.
- The Expo package identifiers are consistently `com.agw`.
- The app root remains consistently `shared/`.
