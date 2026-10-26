// Workspace boundaries: manifest-level rules here, import-level rules in .dependency-cruiser.cjs.
// 工作区边界：manifest 层面的规则在此文件，import 层面的规则在 .dependency-cruiser.cjs 中。
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync, readdirSync, readFileSync } from "node:fs";
import { join, resolve } from "node:path";

const clientsRoot = resolve(import.meta.dirname, "..", "..");
const packagesRoot = join(clientsRoot, "packages");
const MOBILE_SAFE_PACKAGES = new Set([
  "@agw/api",
  "@agw/chat-native",
  "@agw/execution-core",
  "@agw/projects-core",
]);
const APPLICATIONS = ["@agw/web", "@agw/desktop", "@agw/mobile"];

function readManifest(directory) {
  return JSON.parse(readFileSync(join(directory, "package.json"), "utf8"));
}

function workspaceDependencies(manifest) {
  return Object.keys({
    ...manifest.dependencies,
    ...manifest.devDependencies,
    ...manifest.peerDependencies,
  }).filter((name) => name.startsWith("@agw/"));
}

function packageDirectoriesIn(root) {
  return readdirSync(root, { withFileTypes: true })
    .filter((entry) => entry.isDirectory() && existsSync(join(root, entry.name, "package.json")))
    .map((entry) => join(root, entry.name));
}

// packages/extensions/* 是随 Desktop 打包的 extension 页面应用，只依赖 @agw/extension-sdk，也不被任何包依赖。
// packages/extensions/* are extension page apps bundled with Desktop; they depend only on @agw/extension-sdk and
// nothing depends on them.
const extensionPackageDirectories = packageDirectoriesIn(join(packagesRoot, "extensions"));
const extensionPackageNames = new Set(
  extensionPackageDirectories.map((directory) => readManifest(directory).name),
);
for (const directory of extensionPackageDirectories) {
  const manifest = readManifest(directory);
  assert.match(manifest.name, /^@agw\/extension-/u, `${directory} must be named @agw/extension-*`);
  for (const dependency of workspaceDependencies(manifest)) {
    assert.equal(
      dependency,
      "@agw/extension-sdk",
      `${manifest.name} may only depend on @agw/extension-sdk, found ${dependency}`,
    );
  }
}

const packageDirectories = [...packageDirectoriesIn(packagesRoot), ...extensionPackageDirectories];

for (const directory of [
  ...packageDirectories,
  ...["web", "desktop", "mobile"].map((name) => join(clientsRoot, name)),
]) {
  for (const dependency of workspaceDependencies(readManifest(directory))) {
    assert.ok(
      !extensionPackageNames.has(dependency),
      `${readManifest(directory).name} must not depend on the extension package ${dependency}`,
    );
  }
}

// monaco-editor 与 prettier 只由 @agw/code-view 声明；@agw/code-view 只由 @agw/projects、Web 与 Desktop 声明，Mobile
// 因此不会引入 Monaco。
// Only @agw/code-view declares monaco-editor and prettier; only @agw/projects, Web and Desktop declare @agw/code-view,
// so Mobile never brings in Monaco.
const CODE_VIEW_CONSUMERS = new Set(["@agw/projects", "@agw/web", "@agw/desktop"]);
for (const directory of [
  ...packageDirectories,
  ...["web", "desktop", "mobile"].map((name) => join(clientsRoot, name)),
]) {
  const manifest = readManifest(directory);
  const declared = Object.keys({
    ...manifest.dependencies,
    ...manifest.devDependencies,
    ...manifest.peerDependencies,
  });
  for (const dependency of ["monaco-editor", "prettier"]) {
    if (!declared.includes(dependency)) continue;
    assert.ok(
      manifest.name === "@agw/code-view",
      `${manifest.name} must use ${dependency} through @agw/code-view`,
    );
  }
  if (declared.includes("@agw/code-view")) {
    assert.ok(
      CODE_VIEW_CONSUMERS.has(manifest.name),
      `${manifest.name} must not depend on @agw/code-view`,
    );
  }
}

for (const directory of packageDirectories) {
  const manifest = readManifest(directory);
  assert.match(manifest.name, /^@agw\//u, `${directory} must be named @agw/*`);
  for (const dependency of workspaceDependencies(manifest)) {
    assert.ok(
      !APPLICATIONS.includes(dependency),
      `${manifest.name} must not depend on the application ${dependency}`,
    );
  }
}

assert.ok(
  !workspaceDependencies(readManifest(join(clientsRoot, "web"))).includes("@agw/desktop"),
  "@agw/web must not depend on @agw/desktop",
);
assert.ok(
  !workspaceDependencies(readManifest(join(clientsRoot, "desktop"))).includes("@agw/web"),
  "@agw/desktop must not depend on @agw/web",
);
for (const dependency of workspaceDependencies(readManifest(join(clientsRoot, "mobile")))) {
  assert.ok(
    MOBILE_SAFE_PACKAGES.has(dependency),
    `@agw/mobile may only depend on React Native-safe packages, found ${dependency}`,
  );
}

const scanRoots = [
  "web/src",
  "desktop/src",
  "desktop/renderer/src",
  "desktop/scripts",
  "mobile/app",
  "mobile/src",
  ...packageDirectories.map((directory) => join(directory, "src")),
].filter((directory) => existsSync(resolve(clientsRoot, directory)));

const cruise = spawnSync(
  process.platform === "win32" ? "depcruise.cmd" : "depcruise",
  ["--config", ".dependency-cruiser.cjs", ...scanRoots],
  { cwd: clientsRoot, stdio: "inherit" },
);
if (cruise.error) throw cruise.error;
assert.equal(cruise.status, 0, "dependency-cruiser reported boundary violations");

console.log(`Client workspace boundaries valid (${packageDirectories.length} packages).`);
