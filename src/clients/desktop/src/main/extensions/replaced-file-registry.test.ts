import assert from "node:assert/strict";
import test from "node:test";

import { REPLACED_FILE_WINDOW_MS, ReplacedFileRegistry } from "./replaced-file-registry";

test("each replacement gets a new number per path, and records expire after the window", async () => {
  const registry = new ReplacedFileRegistry();
  assert.equal(registry.latest("/data/a.txt"), null);
  registry.record("/data/a.txt");
  const first = registry.latest("/data/a.txt");
  assert.ok(first !== null);
  registry.record("/data/a.txt");
  const second = registry.latest("/data/a.txt");
  assert.ok(second !== null && second > first);
  assert.equal(registry.latest("/data/b.txt"), null);

  await new Promise((resolve) => setTimeout(resolve, REPLACED_FILE_WINDOW_MS + 100));
  assert.equal(registry.latest("/data/a.txt"), null);
});
