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

const packageDirectories = readdirSync(packagesRoot, { withFileTypes: true })
  .filter((entry) => entry.isDirectory() && existsSync(join(packagesRoot, entry.name, "package.json")))
  .map((entry) => join(packagesRoot, entry.name));

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
