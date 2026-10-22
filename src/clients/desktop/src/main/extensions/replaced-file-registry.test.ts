import assert from "node:assert/strict";
import { rename, writeFile } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";

import { createScratchDirectory } from "../scratch-directory.test-helper";
import { REPLACED_FILE_WINDOW_MS, ReplacedFileRegistry } from "./replaced-file-registry";

test("each replacement gets a new number per path, and records expire after the window", async () => {
  const scratch = await createScratchDirectory("replaced-files");
  const registry = new ReplacedFileRegistry();
  const target = join(scratch.path, "a.txt");
  const replacement = join(scratch.path, "a.next");
  try {
    await writeFile(target, "before");
    assert.equal(await registry.latest(target), null);
    await writeFile(replacement, "after");
    const recording = registry.record(target, rename(replacement, target));
    const first = await registry.latest(target);
    await recording;
    assert.ok(first !== null);
    await writeFile(replacement, "again");
    await registry.record(target, rename(replacement, target));
    const second = await registry.latest(target);
    assert.ok(second !== null && second > first);
    assert.equal(await registry.latest(join(scratch.path, "b.txt")), null);

    await new Promise((resolve) => setTimeout(resolve, REPLACED_FILE_WINDOW_MS + 100));
    assert.equal(await registry.latest(target), null);
  } finally {
    await scratch.dispose();
  }
});

test("a failed replacement rejects without recording a target or blocking lookups", async () => {
  const scratch = await createScratchDirectory("replaced-files");
  const registry = new ReplacedFileRegistry();
  const target = join(scratch.path, "a.txt");
  try {
    const recording = registry.record(target, rename(join(scratch.path, "missing.txt"), target));
    const latest = registry.latest(target);
    await assert.rejects(recording, { code: "ENOENT" });
    assert.equal(await latest, null);
  } finally {
    await scratch.dispose();
  }
});
