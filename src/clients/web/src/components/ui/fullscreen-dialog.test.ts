import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const DIALOG_URL = new URL("./dialog.tsx", import.meta.url);
const RUNTIME_URL = new URL("../../lib/desktop-runtime.tsx", import.meta.url);
const GLOBALS_URL = new URL("../../app/globals.css", import.meta.url);

test("Dialog exposes a true fullscreen size", async () => {
  const source = await readFile(DIALOG_URL, "utf8");

  assert.match(source, /fullscreen:\s*"[^"]*top-0 left-0[^"]*h-screen[^"]*w-screen/);
});

test("Desktop runtime exposes renderer and platform markers on the document root", async () => {
  const source = await readFile(RUNTIME_URL, "utf8");

  assert.match(source, /document\.documentElement/);
  assert.match(source, /root\.dataset\.agwDesktop = String\(isDesktop\)/);
  assert.match(source, /root\.dataset\.agwPlatform = platform/);
  assert.match(source, /delete root\.dataset\.agwDesktop/);
  assert.match(source, /delete root\.dataset\.agwPlatform/);
});

test("Desktop fullscreen Dialog headers reserve native window-control space", async () => {
  const source = await readFile(GLOBALS_URL, "utf8");

  assert.match(source, /data-agw-desktop="true"/);
  assert.match(source, /data-agw-platform="darwin"/);
  assert.match(source, /data-size="fullscreen"/);
  assert.match(source, /padding-left: 76px/);
  assert.match(source, /data-agw-platform="win32"/);
  assert.match(source, /data-agw-platform="linux"/);
  assert.match(source, /padding-right: 146px/);
});
