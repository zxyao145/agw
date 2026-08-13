import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import test from "node:test";

test("Desktop exposes packaged version metadata and update checks through the bridge", async () => {
  const [mainSource, preloadSource] = await Promise.all([
    readFile(resolve(process.cwd(), "src/main/index.ts"), "utf8"),
    readFile(resolve(process.cwd(), "src/preload/index.ts"), "utf8"),
  ]);

  assert.match(mainSource, /appVersion: packageMetadata\.appVersion/u);
  assert.match(mainSource, /architecture: process\.arch/u);
  assert.match(mainSource, /ipcMain\.handle\("agw:check-for-updates"/u);
  assert.match(mainSource, /checkForDesktopUpdate\(\(input, init\) => net\.fetch\(input, init\)/u);
  assert.match(preloadSource, /checkForUpdates: \(\) =>/u);
  assert.match(preloadSource, /ipcRenderer\.invoke\("agw:check-for-updates"\)/u);
});
