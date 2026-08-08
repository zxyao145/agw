import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import test from "node:test";

interface PackageManifest {
  main: string;
  scripts: Record<string, string>;
}

test("Desktop owns and develops its React renderer independently", async () => {
  const packageManifest = JSON.parse(
    await readFile(resolve(process.cwd(), "package.json"), "utf8"),
  ) as PackageManifest;
  assert.equal(packageManifest.main, "dist/main/index.js");

  const devCommand = packageManifest.scripts.dev;
  assert.match(devCommand, /node scripts\/dev\.mjs/u);
  assert.doesNotMatch(devCommand, /prepare:renderer|@agw\/web|web\//u);
  assert.match(packageManifest.scripts["build:renderer"], /next build renderer/u);
  assert.match(packageManifest.scripts["dev:renderer"], /next dev renderer/u);
  assert.match(packageManifest.scripts.build, /build:main[\s\S]*build:renderer/u);
  assert.equal(packageManifest.scripts["prepare:renderer"], undefined);
});

test("macOS development app registers the Desktop OAuth protocol", async () => {
  const source = await readFile(resolve(process.cwd(), "scripts/dev.mjs"), "utf8");

  assert.match(source, /CFBundleURLTypes/u);
  assert.match(source, /CFBundleURLSchemes/u);
  assert.match(source, /agw-desktop/u);
  assert.match(source, /codesign/u);
  assert.match(source, /lsregister/u);
  assert.match(source, /join\(homedir\(\), "Applications"\)/u);
  assert.match(source, /\$\{appName\} Development\.app/u);
  assert.doesNotMatch(source, /mkdtemp|tmpdir/u);
});
