import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { join, resolve } from "node:path";
import test from "node:test";

import { createScratchDirectory } from "../scratch-directory.test-helper";

test("copyTypographyStylesheet writes the shared typography.css byte for byte", async () => {
  // 脚本含 top-level await，只能以 ES module 动态导入。The script uses top-level await, so it can only be imported dynamically as an ES module.
  const { copyTypographyStylesheet } = await import("../../../scripts/build-extension-sdk.mjs");
  const scratch = await createScratchDirectory("sdk-build");
  try {
    await copyTypographyStylesheet(scratch.path);

    const source = await readFile(
      resolve("..", "packages", "components", "src", "ui-tokens", "typography.css"),
    );
    const copy = await readFile(join(scratch.path, "typography.css"));
    assert.ok(copy.equals(source));
  } finally {
    await scratch.dispose();
  }
});
