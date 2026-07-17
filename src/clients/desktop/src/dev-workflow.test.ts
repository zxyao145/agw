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
