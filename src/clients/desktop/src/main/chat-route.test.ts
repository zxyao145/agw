import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const MAIN_URL = new URL("./index.ts", import.meta.url);

test("Desktop starts and reopens on the dedicated Chat route", async () => {
  const source = await readFile(MAIN_URL, "utf8");

  assert.match(source, /loadRenderer\(pathname = "\/desktop\/chat\/"\)/);
  assert.match(source, /Open Agw Chat"[\s\S]*?showWindowSafely\("\/desktop\/chat\/"\)/);
  assert.match(source, /tray\.on\("click", \(\) => showWindowSafely\("\/desktop\/chat\/"\)\)/);
  assert.doesNotMatch(source, /showWindowSafely\("\/chat\/"\)/);
});

test("Desktop clears stale development redirects before loading Chat", async () => {
  const source = await readFile(MAIN_URL, "utf8");
  const readyHandler = source.slice(source.indexOf(".whenReady()"));

  assert.match(
    source,
    /!app\.isPackaged && process\.env\.AGW_RENDERER_URL[\s\S]*?session\.defaultSession\.clearCache\(\)/,
  );
  assert.ok(
    readyHandler.indexOf("prepareRendererSession()") <
      readyHandler.indexOf("loadRenderer(initialRoute)"),
    "the development cache should be cleared before the initial renderer navigation",
  );
  assert.match(readyHandler, /\.catch\(\(error\) => reportMainProcessError\(/);
});

test("Desktop diagnoses renderer exits and recovers without a reload loop", async () => {
  const source = await readFile(MAIN_URL, "utf8");

  assert.match(source, /webContents\.on\("render-process-gone"/);
  assert.match(source, /"did-fail-load"/);
  assert.match(source, /window\.on\("unresponsive"/);
  assert.match(source, /window\.on\("responsive"/);
  assert.match(source, /renderer-events\.jsonl/);
  assert.match(source, /buttons: \["Reload Chat", "Close Window"\]/);
  assert.match(source, /rendererReloadRequired = true;[\s\S]*?window\.hide\(\)/);
  assert.match(source, /details\.reason === "clean-exit"/);
  assert.match(source, /destructionPlanned/);
  assert.match(source, /errorCode === -3|ERR_ABORTED/);
});

test("Desktop completes startup after a successful initial renderer recovery", async () => {
  const source = await readFile(MAIN_URL, "utf8");
  const readyHandler = source.slice(source.indexOf(".whenReady()"));

  assert.match(
    source,
    /rendererRecoveryAttempt: Promise<boolean> \| null = null;[\s\S]*?rendererRecoveryAttempt = recovery;/,
  );
  assert.match(
    readyHandler,
    /catch \(error\) \{[\s\S]*?const recovery = rendererRecoveryAttempt;[\s\S]*?if \(!recovery \|\| !\(await recovery\)\) throw error;[\s\S]*?rendererReady = true;/,
  );
});
