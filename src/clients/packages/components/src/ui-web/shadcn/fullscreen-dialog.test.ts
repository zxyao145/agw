import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const DIALOG_URL = new URL("./dialog.tsx", import.meta.url);
const PACKAGES_URL = new URL("../../../../", import.meta.url);
const GLOBALS_URL = new URL("components/src/ui-tokens/tokens.css", PACKAGES_URL);

test("Dialog exposes a true fullscreen size", async () => {
  const source = await readFile(DIALOG_URL, "utf8");

  assert.match(source, /fullscreen:\s*"[^"]*top-0 left-0[^"]*h-screen[^"]*w-screen/);
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
