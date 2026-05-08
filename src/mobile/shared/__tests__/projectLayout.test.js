const fs = require('fs');
const path = require('path');

function findRepoRoot(startPath) {
  let current = startPath;

  while (current !== path.dirname(current)) {
    if (fs.existsSync(path.join(current, '.git'))) {
      return current;
    }

    current = path.dirname(current);
  }

  throw new Error(`Could not find repository root from ${startPath}`);
}

const repoRoot = findRepoRoot(__dirname);

describe('project layout', () => {
  it('keeps Swift/iOS code under ios and React Native code under shared', () => {
    expect(fs.existsSync(path.join(repoRoot, 'ios', 'Fe.xcodeproj'))).toBe(true);
    expect(fs.existsSync(path.join(repoRoot, 'ios', 'Fe', 'ReactViewController.swift'))).toBe(true);
    expect(fs.existsSync(path.join(repoRoot, 'shared', 'index.js'))).toBe(true);
    expect(fs.existsSync(path.join(repoRoot, 'shared', 'src', 'rn', 'App.tsx'))).toBe(true);

    expect(fs.existsSync(path.join(repoRoot, 'Fe'))).toBe(false);
    expect(fs.existsSync(path.join(repoRoot, 'src', 'rn'))).toBe(false);
  });

  it('uses native tabs for the top-level React Native pages', () => {
    const contentView = fs.readFileSync(
      path.join(repoRoot, 'ios', 'Fe', 'ContentView.swift'),
      'utf8',
    );

    expect(contentView).toContain('TabView');
    expect(contentView).toContain('.tabItem');
    expect(contentView).not.toContain('.sheet');
    expect(contentView).not.toContain('selectedPage');
  });

  it('keeps Android native code under android and points it at shared React Native', () => {
    const androidMainActivity = fs.readFileSync(
      path.join(repoRoot, 'android', 'app', 'src', 'main', 'java', 'com', 'fe', 'MainActivity.kt'),
      'utf8',
    );
    const androidBuild = fs.readFileSync(
      path.join(repoRoot, 'android', 'app', 'build.gradle'),
      'utf8',
    );
    const androidSettings = fs.readFileSync(
      path.join(repoRoot, 'android', 'settings.gradle'),
      'utf8',
    );
    const reactNativeConfig = fs.readFileSync(
      path.join(repoRoot, 'shared', 'react-native.config.js'),
      'utf8',
    );

    expect(androidMainActivity).toContain('getMainComponentName(): String = "FeReactNative"');
    expect(androidBuild).toContain('root = file("../../shared")');
    expect(androidBuild).toContain('entryFile = file("../../shared/index.js")');
    expect(androidSettings).toContain('includeBuild("../shared/node_modules/@react-native/gradle-plugin")');
    expect(reactNativeConfig).toContain("android: {");
    expect(reactNativeConfig).toContain("sourceDir: '../android'");
  });

  it('uses a Windows-compatible React Native autolinking command', () => {
    const androidSettings = fs.readFileSync(
      path.join(repoRoot, 'android', 'settings.gradle'),
      'utf8',
    );

    expect(androidSettings).toContain('reactNativeCliConfigCommand');
    expect(androidSettings).toContain('["cmd", "/c", "npx", "@react-native-community/cli", "config"]');
    expect(androidSettings).toContain('reactNativeCliConfigCommand,');
  });
});
