import assert from "node:assert/strict";
import { mkdir, rename, symlink, unlink, writeFile } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";

import { createScratchDirectory } from "../scratch-directory.test-helper";
import { packageDigest } from "./extension-package-digest";

test("the package digest changes with any file content, name, addition or link target", async () => {
  const scratch = await createScratchDirectory("package-digest");
  try {
    const root = join(scratch.path, "package");
    await mkdir(join(root, "assets"), { recursive: true });
    await writeFile(join(root, "manifest.json"), '{"id":"sample"}');
    await writeFile(join(root, "assets", "app.js"), "export {};");
    await writeFile(join(root, "assets", "other.js"), "export const a = 1;");
    await symlink("app.js", join(root, "assets", "link.js"));

    const first = await packageDigest(root);
    assert.match(first, /^[0-9a-f]{64}$/u);
    assert.equal(await packageDigest(root), first);

    await writeFile(join(root, "assets", "app.js"), "export {} ;");
    const changed = await packageDigest(root);
    assert.notEqual(changed, first);

    await rename(join(root, "assets", "other.js"), join(root, "assets", "renamed.js"));
    const renamed = await packageDigest(root);
    assert.notEqual(renamed, changed);

    await writeFile(join(root, "added.txt"), "");
    const added = await packageDigest(root);
    assert.notEqual(added, renamed);

    await unlink(join(root, "assets", "link.js"));
    await symlink("renamed.js", join(root, "assets", "link.js"));
    assert.notEqual(await packageDigest(root), added);
  } finally {
    await scratch.dispose();
  }
});
