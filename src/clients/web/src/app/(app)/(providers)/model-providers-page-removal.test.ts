import assert from "node:assert/strict";
import { access, readFile } from "node:fs/promises";
import test from "node:test";

const ROUTE_URL = new URL("./model-providers/page.tsx", import.meta.url);
const LAYOUT_URL = new URL("../layout.tsx", import.meta.url);

test("model providers management page is removed from the web app", async () => {
  await assert.rejects(access(ROUTE_URL));

  const layoutSource = await readFile(LAYOUT_URL, "utf8");
  assert.doesNotMatch(layoutSource, /url: "\/model-providers"/);
});
