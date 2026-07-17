import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const LAYOUT_URL = new URL("./layout.tsx", import.meta.url);

test("Web application owns only the browser shell", async () => {
  const source = await readFile(LAYOUT_URL, "utf8");

  assert.match(source, /<SidebarProvider/);
  assert.doesNotMatch(source, /useDesktopRuntime|DesktopConnectionGate|<AppShell>/);
});
