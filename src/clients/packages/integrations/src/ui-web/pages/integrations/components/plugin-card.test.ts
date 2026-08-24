import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const COMPONENT_URL = new URL("./plugin-card.tsx", import.meta.url);

test("plugin card only exposes shared setup configuration to administrators", async () => {
  const source = await readFile(COMPONENT_URL, "utf8");

  assert.match(source, /canConfigureInstallation \? \(/);
  assert.match(source, />\s*Configure\s*</);
});
