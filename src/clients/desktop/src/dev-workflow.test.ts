import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import test from "node:test";

interface PackageManifest {
  main: string;
  scripts: Record<string, string>;
}

test("dev prepares the bundled renderer before Electron starts", async () => {
  const packageManifest = JSON.parse(
    await readFile(resolve(process.cwd(), "package.json"), "utf8"),
  ) as PackageManifest;
  assert.equal(packageManifest.main, "dist/main/index.js");

  const devCommand = packageManifest.scripts.dev;
  const prepareRendererIndex = devCommand.indexOf("pnpm prepare:renderer");
  const electronIndex = devCommand.indexOf("electron .");

  assert.notEqual(prepareRendererIndex, -1);
  assert.ok(prepareRendererIndex < electronIndex);
  assert.equal(
    packageManifest.scripts["prepare:renderer"],
    "node scripts/prepare-resources.mjs --renderer-only",
  );
});
