import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const PAGE_URL = new URL("./page.tsx", import.meta.url);

test("integrations page renders connected instances above an adaptive app catalog", async () => {
  const source = await readFile(PAGE_URL, "utf8");

  assert.match(source, /<AppInstanceCard/);
  assert.match(source, /Connected apps/);
  assert.match(source, /<AppDefinitionCard/);
  assert.match(source, /grid-cols-\[repeat\(auto-fit,minmax\(280px,400px\)\)\]/);
  assert.match(source, /<CreateConnectionDialog/);
  assert.match(source, /callbackUrl=\{callbackUrl\}/);
});
