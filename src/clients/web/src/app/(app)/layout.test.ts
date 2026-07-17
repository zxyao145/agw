import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const LAYOUT_URL = new URL("./layout.tsx", import.meta.url);

test("browser routes keep the Web sidebar instead of the Desktop shell", async () => {
  const source = await readFile(LAYOUT_URL, "utf8");

  assert.match(source, /const desktop = useDesktopRuntime\(\);/);
  assert.match(source, /if \(desktop\.isDesktop\) \{[\s\S]*?<AppShell>/);
  assert.match(source, /<SidebarProvider/);
});
