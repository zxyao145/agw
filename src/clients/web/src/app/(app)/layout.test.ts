import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const LAYOUT_URL = new URL("./layout.tsx", import.meta.url);

test("Web application owns only the browser shell", async () => {
  const source = await readFile(LAYOUT_URL, "utf8");

  assert.match(source, /<SidebarProvider/);
  assert.doesNotMatch(source, /useDesktopRuntime|DesktopConnectionGate|<AppShell>/);
});

test("Web Chat constrains its workspace to the viewport", async () => {
  const source = await readFile(LAYOUT_URL, "utf8");

  assert.match(source, /const isChatRoute = pathname === "\/chat"/);
  assert.match(source, /className=\{cn\(isChatRoute && "h-dvh overflow-hidden"\)\}/);
  assert.match(source, /isChatRoute \? "h-dvh overflow-hidden" : "min-h-screen"/);
  assert.match(source, /isChatRoute \? "h-full min-h-0 overflow-y-hidden" : "min-h-screen"/);
  assert.match(source, /isChatRoute \? "overflow-hidden" : "overflow-x-hidden"/);
});
