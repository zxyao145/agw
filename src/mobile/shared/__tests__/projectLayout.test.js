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
    const appConfig = JSON.parse(
      fs.readFileSync(path.join(sharedRoot, 'app.json'), 'utf8'),
    );
    const packageJson = JSON.parse(
      fs.readFileSync(path.join(sharedRoot, 'package.json'), 'utf8'),
    );

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
    expect(
      fs.existsSync(
        path.join(repoRoot, 'android', 'app', 'src', 'main', 'java', 'com', 'agw', 'MainActivity.kt'),
      ),
    ).toBe(false);
  });
});
