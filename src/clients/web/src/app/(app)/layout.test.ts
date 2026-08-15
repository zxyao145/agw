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

test("Web groups User Memory and shared tools under Capabilities", async () => {
  const source = await readFile(LAYOUT_URL, "utf8");

  assert.match(source, /groupLable: "Agent & Flow"[\s\S]*title: "Agentflows"/);
  assert.match(
    source,
    /groupLable: "Capabilities"[\s\S]*url: "\/user-memory"[\s\S]*title: "Skills"[\s\S]*title: "MCP Tool Servers"[\s\S]*title: "Integrations"/,
  );
  assert.equal((source.match(/groupLable: "Capabilities"/g) ?? []).length, 1);
});
