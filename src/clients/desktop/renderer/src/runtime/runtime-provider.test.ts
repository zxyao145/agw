import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const RUNTIME_URL = new URL("./runtime-provider.tsx", import.meta.url);

test("Desktop runtime exposes renderer and platform markers on the document root", async () => {
  const source = await readFile(RUNTIME_URL, "utf8");

  assert.match(source, /document\.documentElement/);
  assert.match(source, /root\.dataset\.agwDesktop = String\(isDesktop\)/);
  assert.match(source, /root\.dataset\.agwPlatform = platform/);
  assert.match(source, /delete root\.dataset\.agwDesktop/);
  assert.match(source, /delete root\.dataset\.agwPlatform/);
});
