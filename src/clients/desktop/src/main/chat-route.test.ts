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
