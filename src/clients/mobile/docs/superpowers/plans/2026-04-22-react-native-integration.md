# React Native Integration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Embed React Native 0.85 into the existing SwiftUI iOS app so multiple Swift buttons can open different React Native pages.

**Architecture:** Keep SwiftUI as the native shell. Swift passes a `routeName`, `title`, and page-specific props into one registered React Native module named `AgwReactNative`; the RN root dispatches that route to separate screen components.

**Tech Stack:** SwiftUI, UIKit, React Native 0.85.2, React 19.2.3, TypeScript, Jest, CocoaPods 1.16.2, Xcode workspace generated from `Agw.xcodeproj`.

---

## File Structure

- Create `package.json`: React Native npm dependencies and scripts.
- Create `app.json`: RN module name and display name.
- Create `babel.config.js`, `jest.config.js`, `tsconfig.json`, `metro.config.js`, `.watchmanconfig`: RN TypeScript, Jest, Babel, and Metro config.
- Create `.gitignore`: ignore generated JS, CocoaPods, Xcode, and local bundle artifacts.
- Create `src/rn/routes.ts`: page route registry and route resolution.
- Create `src/rn/App.tsx`: RN root component that renders the requested route.
- Create `index.js`: RN entrypoint that registers `AgwReactNative`.
- Create `__tests__/routes.test.ts` and `__tests__/App.test.tsx`: route and root rendering tests.
- Create `Gemfile`, `Podfile`, `react-native.config.js`: CocoaPods and RN CLI iOS configuration for a root-level Xcode project.
- Create `Agw/ReactNativePage.swift`: Swift page configuration model.
- Create `Agw/ReactNativeView.swift`: SwiftUI wrapper for the RN UIKit controller.
- Create `Agw/ReactViewController.swift`: UIKit controller that owns `RCTReactNativeFactory`.
- Modify `Agw/ContentView.swift`: native multi-button launcher UI.
- Modify `Agw.xcodeproj/project.pbxproj`: set RN-required build settings and add the release JS bundle phase.

---

### Task 1: JavaScript Tooling And Route Registry

**Files:**
- Create: `package.json`
- Create: `app.json`
- Create: `babel.config.js`
- Create: `jest.config.js`
- Create: `tsconfig.json`
- Create: `metro.config.js`
- Create: `.watchmanconfig`
- Create: `.gitignore`
- Create: `__tests__/routes.test.ts`
- Create: `src/rn/routes.ts`

- [ ] **Step 1: Add React Native package and tool config**

Create `package.json`:

```json
{
  "name": "agw-react-native-bridge",
  "version": "0.0.1",
  "private": true,
  "scripts": {
    "start": "react-native start",
    "test": "jest",
    "typecheck": "tsc --noEmit"
  },
  "dependencies": {
    "react": "19.2.3",
    "react-native": "0.85.2",
    "react-native-safe-area-context": "^5.5.2"
  },
  "devDependencies": {
    "@babel/core": "^7.25.2",
    "@babel/preset-env": "^7.25.3",
    "@babel/runtime": "^7.25.0",
    "@react-native-community/cli": "20.1.0",
    "@react-native-community/cli-platform-ios": "20.1.0",
    "@react-native/babel-preset": "0.85.2",
    "@react-native/jest-preset": "0.85.2",
    "@react-native/metro-config": "0.85.2",
    "@react-native/typescript-config": "0.85.2",
    "@types/jest": "^29.5.13",
    "@types/react": "^19.2.0",
    "@types/react-test-renderer": "^19.1.0",
    "jest": "^29.6.3",
    "prettier": "2.8.8",
    "react-test-renderer": "19.2.3",
    "typescript": "^5.8.3"
  },
  "engines": {
    "node": ">=22.11.0"
  }
}
```

Create `app.json`:

```json
{
  "name": "AgwReactNative",
  "displayName": "Agw"
}
```

Create `babel.config.js`:

```js
module.exports = {
  presets: ['module:@react-native/babel-preset'],
};
```

Create `jest.config.js`:

```js
module.exports = {
  preset: '@react-native/jest-preset',
};
```

Create `tsconfig.json`:

```json
{
  "extends": "@react-native/typescript-config",
  "compilerOptions": {
    "types": ["jest"]
  },
  "include": ["**/*.ts", "**/*.tsx"],
  "exclude": ["**/node_modules", "**/Pods"]
}
```

Create `metro.config.js`:

```js
const {getDefaultConfig, mergeConfig} = require('@react-native/metro-config');

const config = {};

module.exports = mergeConfig(getDefaultConfig(__dirname), config);
```

Create `.watchmanconfig`:

```json
{}
```

Create `.gitignore`:

```gitignore
.DS_Store
node_modules/
Pods/
vendor/bundle/
coverage/
build/
DerivedData/
*.xcuserstate
*.xcworkspace/xcuserdata/
npm-debug.log*
yarn-debug.log*
yarn-error.log*
main.jsbundle
assets/
```

- [ ] **Step 2: Install JavaScript dependencies**

Run:

```bash
npm install
```

Expected: `package-lock.json` is created and npm exits with code 0.

- [ ] **Step 3: Write the failing route registry test**

Create `__tests__/routes.test.ts`:

```ts
import {resolveRoute} from '../src/rn/routes';

describe('resolveRoute', () => {
  it('returns a configured React Native page for a known route', () => {
    expect(resolveRoute('settings')).toEqual({
      routeName: 'settings',
      title: 'Settings',
      description: 'Manage preferences from a React Native screen.',
      accentColor: '#2563eb',
    });
  });

  it('returns undefined for an unknown route', () => {
    expect(resolveRoute('missing')).toBeUndefined();
  });
});
```

- [ ] **Step 4: Run the route test and verify it fails**

Run:

```bash
npm test -- --runTestsByPath __tests__/routes.test.ts
```

Expected: FAIL with `Cannot find module '../src/rn/routes'`.

- [ ] **Step 5: Implement the route registry**

Create `src/rn/routes.ts`:

```ts
export type RouteName = 'home' | 'settings' | 'details';

export type ReactNativeInitialProps = {
  routeName?: string;
  title?: string;
  source?: string;
};

export type RouteDefinition = {
  routeName: RouteName;
  title: string;
  description: string;
  accentColor: string;
};

export const routes: Record<RouteName, RouteDefinition> = {
  home: {
    routeName: 'home',
    title: 'Home',
    description: 'A React Native landing page opened from SwiftUI.',
    accentColor: '#16a34a',
  },
  settings: {
    routeName: 'settings',
    title: 'Settings',
    description: 'Manage preferences from a React Native screen.',
    accentColor: '#2563eb',
  },
  details: {
    routeName: 'details',
    title: 'Details',
    description: 'Inspect route-specific data passed by the native app.',
    accentColor: '#dc2626',
  },
};

export function resolveRoute(routeName?: string): RouteDefinition | undefined {
  if (routeName === 'home' || routeName === 'settings' || routeName === 'details') {
    return routes[routeName];
  }

  return undefined;
}
```

- [ ] **Step 6: Run the route test and verify it passes**

Run:

```bash
npm test -- --runTestsByPath __tests__/routes.test.ts
```

Expected: PASS with 2 passing tests.

- [ ] **Step 7: Commit the JS tooling and route registry**

Run:

```bash
git add .gitignore .watchmanconfig app.json babel.config.js jest.config.js metro.config.js package.json package-lock.json tsconfig.json __tests__/routes.test.ts src/rn/routes.ts
git commit -m "feat: add react native route registry"
```

---

### Task 2: React Native Root Module And Screens

**Files:**
- Create: `__tests__/App.test.tsx`
- Create: `src/rn/App.tsx`
- Create: `index.js`

- [ ] **Step 1: Write the failing RN root rendering test**

Create `__tests__/App.test.tsx`:

```tsx
import React from 'react';
import renderer from 'react-test-renderer';
import App from '../src/rn/App';

describe('App', () => {
  it('renders the selected route from initial props', () => {
    const tree = renderer
      .create(<App routeName="details" title="Native detail" source="SwiftUI" />)
      .toJSON();

    const output = JSON.stringify(tree);

    expect(output).toContain('Native detail');
    expect(output).toContain('Inspect route-specific data passed by the native app.');
    expect(output).toContain('Opened from SwiftUI');
  });

  it('renders a fallback screen for an unknown route', () => {
    const tree = renderer.create(<App routeName="missing" title="Missing" />).toJSON();

    expect(JSON.stringify(tree)).toContain('Unknown route: missing');
  });
});
```

- [ ] **Step 2: Run the RN root test and verify it fails**

Run:

```bash
npm test -- --runTestsByPath __tests__/App.test.tsx
```

Expected: FAIL with `Cannot find module '../src/rn/App'`.

- [ ] **Step 3: Implement the RN root component**

Create `src/rn/App.tsx`:

```tsx
import React from 'react';
import {
  StatusBar,
  StyleSheet,
  Text,
  useColorScheme,
  View,
} from 'react-native';
import {
  SafeAreaProvider,
  useSafeAreaInsets,
} from 'react-native-safe-area-context';
import {ReactNativeInitialProps, resolveRoute} from './routes';

function App(props: ReactNativeInitialProps): React.JSX.Element {
  const isDarkMode = useColorScheme() === 'dark';

  return (
    <SafeAreaProvider>
      <StatusBar barStyle={isDarkMode ? 'light-content' : 'dark-content'} />
      <RouteScreen {...props} />
    </SafeAreaProvider>
  );
}

function RouteScreen(props: ReactNativeInitialProps): React.JSX.Element {
  const safeAreaInsets = useSafeAreaInsets();
  const route = resolveRoute(props.routeName);

  if (!route) {
    return (
      <View style={[styles.container, {paddingTop: safeAreaInsets.top + 24}]}>
        <Text style={styles.eyebrow}>Agw React Native</Text>
        <Text style={styles.title}>Unknown route: {props.routeName ?? 'none'}</Text>
        <Text style={styles.body}>Swift opened a route that is not registered in JavaScript.</Text>
      </View>
    );
  }

  return (
    <View
      style={[
        styles.container,
        {paddingTop: safeAreaInsets.top + 24, borderTopColor: route.accentColor},
      ]}>
      <Text style={styles.eyebrow}>Agw React Native</Text>
      <Text style={styles.title}>{props.title ?? route.title}</Text>
      <Text style={styles.body}>{route.description}</Text>
      <View style={[styles.badge, {backgroundColor: route.accentColor}]}>
        <Text style={styles.badgeText}>Route: {route.routeName}</Text>
      </View>
      <Text style={styles.meta}>Opened from {props.source ?? 'native'}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    borderTopWidth: 6,
    paddingHorizontal: 24,
    backgroundColor: '#f8fafc',
  },
  eyebrow: {
    color: '#475569',
    fontSize: 13,
    fontWeight: '600',
    letterSpacing: 0,
    marginBottom: 12,
    textTransform: 'uppercase',
  },
  title: {
    color: '#0f172a',
    fontSize: 34,
    fontWeight: '700',
    letterSpacing: 0,
    marginBottom: 16,
  },
  body: {
    color: '#334155',
    fontSize: 17,
    lineHeight: 25,
    marginBottom: 24,
  },
  badge: {
    alignSelf: 'flex-start',
    borderRadius: 6,
    paddingHorizontal: 12,
    paddingVertical: 8,
  },
  badgeText: {
    color: '#ffffff',
    fontSize: 14,
    fontWeight: '700',
  },
  meta: {
    color: '#64748b',
    fontSize: 15,
    marginTop: 20,
  },
});

export default App;
```

- [ ] **Step 4: Register the React Native module**

Create `index.js`:

```js
/**
 * @format
 */

import {AppRegistry} from 'react-native';
import App from './src/rn/App';
import {name as appName} from './app.json';

AppRegistry.registerComponent(appName, () => App);
```

- [ ] **Step 5: Run RN tests and typecheck**

Run:

```bash
npm test -- --runTestsByPath __tests__/routes.test.ts __tests__/App.test.tsx
npm run typecheck
```

Expected: both Jest test files pass and TypeScript exits with code 0.

- [ ] **Step 6: Commit the RN root module**

Run:

```bash
git add __tests__/App.test.tsx src/rn/App.tsx index.js
git commit -m "feat: add react native root screens"
```

---

### Task 3: CocoaPods And React Native iOS Dependency Setup

**Files:**
- Create: `Gemfile`
- Create: `Podfile`
- Create: `react-native.config.js`
- Create after install: `Gemfile.lock`
- Create after install: `Podfile.lock`
- Generate locally: `Agw.xcworkspace`

- [ ] **Step 1: Add CocoaPods and RN CLI configuration**

Create `Gemfile`:

```ruby
source 'https://rubygems.org'

ruby '>= 2.6.10'

gem 'cocoapods', '1.16.2'
gem 'activesupport', '>= 6.1.7.5', '!= 7.1.0'
gem 'xcodeproj', '1.27.0'
gem 'concurrent-ruby', '< 1.3.4'
gem 'bigdecimal'
gem 'logger'
gem 'benchmark'
gem 'mutex_m'
gem 'nkf'
```

Create `Podfile`:

```ruby
# Resolve react_native_pods.rb with node to allow for hoisted installs.
require Pod::Executable.execute_command('node', ['-p',
  'require.resolve(
    "react-native/scripts/react_native_pods.rb",
    {paths: [process.argv[1]]},
  )', __dir__]).strip

platform :ios, min_ios_version_supported
prepare_react_native_project!

target 'Agw' do
  config = use_native_modules!

  use_react_native!(
    :path => config[:reactNativePath],
    :app_path => Pod::Config.instance.installation_root.to_s
  )

  post_install do |installer|
    react_native_post_install(
      installer,
      config[:reactNativePath],
      :mac_catalyst_enabled => false
    )
  end
end
```

Create `react-native.config.js`:

```js
module.exports = {
  project: {
    ios: {
      sourceDir: '.',
      xcodeProject: {
        name: 'Agw.xcodeproj',
      },
    },
  },
};
```

- [ ] **Step 2: Install Ruby dependencies**

Run:

```bash
bundle install
```

Expected: `Gemfile.lock` is created and Bundler exits with code 0.

- [ ] **Step 3: Install iOS Pods**

Run:

```bash
bundle exec pod install
```

Expected: `Podfile.lock` and `Agw.xcworkspace` are created. Output includes `Pod installation complete`.

- [ ] **Step 4: Verify the workspace exists**

Run:

```bash
xcodebuild -list -workspace Agw.xcworkspace
```

Expected: output lists scheme `Agw`.

- [ ] **Step 5: Commit dependency configuration**

Run:

```bash
git add Gemfile Gemfile.lock Podfile Podfile.lock react-native.config.js Agw.xcworkspace
git commit -m "build: add react native ios dependencies"
```

---

### Task 4: Native SwiftUI Multi-Page Launcher And RN Host

**Files:**
- Create: `Agw/ReactNativePage.swift`
- Create: `Agw/ReactNativeView.swift`
- Create: `Agw/ReactViewController.swift`
- Modify: `Agw/ContentView.swift`

- [ ] **Step 1: Add native RN host files and launcher UI**

Create `Agw/ReactNativePage.swift`:

```swift
import Foundation

struct ReactNativePage: Identifiable {
    let id: String
    let routeName: String
    let title: String
    let initialProps: [String: Any]

    init(routeName: String, title: String, initialProps: [String: Any] = [:]) {
        self.id = routeName
        self.routeName = routeName
        self.title = title
        self.initialProps = initialProps
    }

    var props: [String: Any] {
        var props = initialProps
        props["routeName"] = routeName
        props["title"] = title
        props["source"] = "SwiftUI"
        return props
    }

    static let samples: [ReactNativePage] = [
        ReactNativePage(routeName: "home", title: "Home"),
        ReactNativePage(routeName: "settings", title: "Settings"),
        ReactNativePage(routeName: "details", title: "Details", initialProps: [
            "itemId": "FE-42"
        ]),
    ]
}
```

Create `Agw/ReactNativeView.swift`:

```swift
import SwiftUI

struct ReactNativeView: UIViewControllerRepresentable {
    let page: ReactNativePage

    func makeUIViewController(context: Context) -> ReactViewController {
        ReactViewController(page: page)
    }

    func updateUIViewController(_ uiViewController: ReactViewController, context: Context) {
    }
}
```

Create `Agw/ReactViewController.swift`:

```swift
import UIKit
import React
import React_RCTAppDelegate
import ReactAppDependencyProvider

final class ReactViewController: UIViewController {
    private let page: ReactNativePage
    private var reactNativeFactory: RCTReactNativeFactory?
    private var reactNativeFactoryDelegate: RCTReactNativeFactoryDelegate?

    init(page: ReactNativePage) {
        self.page = page
        super.init(nibName: nil, bundle: nil)
        title = page.title
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    override func viewDidLoad() {
        super.viewDidLoad()

        let delegate = ReactNativeDelegate()
        delegate.dependencyProvider = RCTAppDependencyProvider()

        let factory = RCTReactNativeFactory(delegate: delegate)
        reactNativeFactoryDelegate = delegate
        reactNativeFactory = factory

        view = factory.rootViewFactory.view(
            withModuleName: "AgwReactNative",
            initialProperties: page.props
        )
    }
}

final class ReactNativeDelegate: RCTDefaultReactNativeFactoryDelegate {
    override func sourceURL(for bridge: RCTBridge) -> URL? {
        bundleURL()
    }

    override func bundleURL() -> URL? {
        #if DEBUG
        RCTBundleURLProvider.sharedSettings().jsBundleURL(forBundleRoot: "index")
        #else
        Bundle.main.url(forResource: "main", withExtension: "jsbundle")
        #endif
    }
}
```

Replace `Agw/ContentView.swift` with:

```swift
import SwiftUI

struct ContentView: View {
    @State private var selectedPage: ReactNativePage?

    private let pages = ReactNativePage.samples

    var body: some View {
        NavigationStack {
            List(pages) { page in
                Button {
                    selectedPage = page
                } label: {
                    VStack(alignment: .leading, spacing: 4) {
                        Text(page.title)
                            .font(.headline)
                        Text("Open \(page.routeName) in React Native")
                            .font(.subheadline)
                            .foregroundStyle(.secondary)
                    }
                    .padding(.vertical, 6)
                }
            }
            .navigationTitle("Agw")
            .sheet(item: $selectedPage) { page in
                NavigationStack {
                    ReactNativeView(page: page)
                        .navigationTitle(page.title)
                        .toolbar {
                            ToolbarItem(placement: .cancellationAction) {
                                Button("Close") {
                                    selectedPage = nil
                                }
                            }
                        }
                }
            }
        }
    }
}

#Preview {
    ContentView()
}
```

- [ ] **Step 2: Run build and verify the expected project-setting failure**

Run:

```bash
xcodebuild -workspace Agw.xcworkspace -scheme Agw -destination 'platform=iOS Simulator,name=iPhone 17,OS=26.4.1' build
```

Expected: FAIL if user script sandboxing or missing RN build settings still block the Pods/RN scripts. If the build fails earlier with a Swift error, fix the Swift error before continuing.

- [ ] **Step 3: Commit native source files after Swift syntax is clean**

Run only after Swift syntax errors are resolved:

```bash
git add Agw/ReactNativePage.swift Agw/ReactNativeView.swift Agw/ReactViewController.swift Agw/ContentView.swift
git commit -m "feat: add swift react native launcher"
```

---

### Task 5: Xcode Build Settings And Release Bundle Phase

**Files:**
- Modify: `Agw.xcodeproj/project.pbxproj`

- [ ] **Step 1: Update the Xcode project with xcodeproj**

Run:

```bash
bundle exec ruby -rxcodeproj <<'RUBY'
project = Xcodeproj::Project.open('Agw.xcodeproj')
target = project.targets.find { |candidate| candidate.name == 'Agw' }
raise 'Target Agw not found' unless target

target.build_configurations.each do |configuration|
  configuration.build_settings['ENABLE_USER_SCRIPT_SANDBOXING'] = 'NO'
  configuration.build_settings['INFOPLIST_KEY_UIViewControllerBasedStatusBarAppearance'] = 'NO'
end

phase = target.shell_script_build_phases.find { |candidate| candidate.name == 'Bundle React Native code and images' }
phase ||= target.new_shell_script_build_phase('Bundle React Native code and images')
phase.shell_script = <<~'SH'
set -e

WITH_ENVIRONMENT="$REACT_NATIVE_PATH/scripts/xcode/with-environment.sh"
REACT_NATIVE_XCODE="$REACT_NATIVE_PATH/scripts/react-native-xcode.sh"
/bin/sh -c "$WITH_ENVIRONMENT $REACT_NATIVE_XCODE"
SH

target.build_phases.delete(phase)
resources_index = target.build_phases.index { |candidate| candidate.isa == 'PBXResourcesBuildPhase' }
insert_index = resources_index || target.build_phases.length
target.build_phases.insert(insert_index, phase)

project.save
RUBY
```

Expected: command exits with code 0 and `Agw.xcodeproj/project.pbxproj` changes.

- [ ] **Step 2: Refresh Pods after project changes**

Run:

```bash
bundle exec pod install
```

Expected: command exits with code 0 and keeps `Podfile.lock` consistent.

- [ ] **Step 3: Build the iOS workspace**

Run:

```bash
xcodebuild -workspace Agw.xcworkspace -scheme Agw -destination 'platform=iOS Simulator,name=iPhone 17,OS=26.4.1' build
```

Expected: build exits with code 0.

- [ ] **Step 4: Commit Xcode integration settings**

Run:

```bash
git add Agw.xcodeproj/project.pbxproj Agw.xcworkspace Podfile.lock
git commit -m "build: configure xcode for react native"
```

---

### Task 6: End-To-End Verification

**Files:**
- No new files.

- [ ] **Step 1: Run all JavaScript checks**

Run:

```bash
npm test
npm run typecheck
```

Expected: Jest and TypeScript both exit with code 0.

- [ ] **Step 2: Run the simulator build**

Run:

```bash
xcodebuild -workspace Agw.xcworkspace -scheme Agw -destination 'platform=iOS Simulator,name=iPhone 17,OS=26.4.1' build
```

Expected: build exits with code 0.

- [ ] **Step 3: Start Metro for manual verification**

Run:

```bash
npm start
```

Expected: Metro starts and prints that it is waiting on port 8081.

- [ ] **Step 4: Run the app from Xcode**

Open `Agw.xcworkspace`, choose the `Agw` scheme, and run on an iOS simulator. Tap each native row:

```text
Home
Settings
Details
```

Expected:

```text
Home opens an RN screen with Route: home.
Settings opens an RN screen with Route: settings.
Details opens an RN screen with Route: details.
Each RN screen says Opened from SwiftUI.
The Close button dismisses the sheet and returns to the SwiftUI list.
```

- [ ] **Step 5: Commit any verification fixes**

If verification required code changes, commit them:

```bash
git add .
git commit -m "fix: complete react native integration verification"
```

If no code changes were needed, do not create an empty commit.

