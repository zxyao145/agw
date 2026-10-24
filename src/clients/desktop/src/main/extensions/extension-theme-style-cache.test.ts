import assert from "node:assert/strict";
import { mkdir, rename, rm, symlink, unlink, writeFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import test from "node:test";
import { setTimeout as delay } from "node:timers/promises";

import { createScratchDirectory } from "../scratch-directory.test-helper";
import { ThemeCompileError } from "./extension-theme-resources";
import { ThemeStyleCompiler } from "./extension-theme-style-cache";

async function writeFiles(root: string, files: Record<string, string>): Promise<void> {
  for (const [path, content] of Object.entries(files)) {
    const file = join(root, ...path.split("/"));
    await mkdir(dirname(file), { recursive: true });
    await writeFile(file, content);
  }
}

const THEME = {
  "theme.css": '@import "parts/a.css";\n.root { background: url(images/a.svg); }',
  "parts/a.css": ".a { color: red; }",
  "images/a.svg": '<svg xmlns="http://www.w3.org/2000/svg"><rect width="1" height="1"/></svg>',
};

async function withTheme(
  run: (root: string, compiler: ThemeStyleCompiler, scratch: string) => Promise<void>,
  compiler = new ThemeStyleCompiler(),
): Promise<void> {
  const scratch = await createScratchDirectory("theme-cache");
  try {
    const root = join(scratch.path, "theme");
    await writeFiles(root, THEME);
    await run(root, compiler, scratch.path);
  } finally {
    await compiler.invalidate(join(scratch.path, "theme"));
    await scratch.dispose();
  }
}

test("compiles reuse the cache while every dependency is unchanged", () =>
  withTheme(async (root, compiler) => {
    const first = await compiler.compile(root, "theme.css");
    const second = await compiler.compile(root, "theme.css");
    assert.equal(second, first);
    assert.deepEqual(compiler.stats(), { entries: 1, bytes: first.byteLength });
  }));

test("changing an imported file or a resource recompiles", () =>
  withTheme(async (root, compiler) => {
    const first = await compiler.compile(root, "theme.css");
    await writeFile(join(root, "parts", "a.css"), ".a { color: blue; }");
    const second = await compiler.compile(root, "theme.css");
    assert.notEqual(second.styleId, first.styleId);
    assert.ok(second.cssText.includes("blue"));
    await writeFile(
      join(root, "images", "a.svg"),
      '<svg xmlns="http://www.w3.org/2000/svg"><circle r="1"/></svg>',
    );
    const third = await compiler.compile(root, "theme.css");
    assert.notEqual(third.styleId, second.styleId);
  }));

test("a symbolic link that points to another file recompiles", () =>
  withTheme(async (root, compiler) => {
    await writeFiles(root, { "parts/b.css": ".b { color: green; }" });
    await writeFile(join(root, "theme.css"), '@import "parts/link.css";');
    await symlink("a.css", join(root, "parts", "link.css"));
    const first = await compiler.compile(root, "theme.css");
    assert.ok(first.cssText.includes(".a"));
    await unlink(join(root, "parts", "link.css"));
    await symlink("b.css", join(root, "parts", "link.css"));
    const second = await compiler.compile(root, "theme.css");
    assert.ok(second.cssText.includes(".b"));
  }));

test("a deleted dependency fails the next compile", () =>
  withTheme(async (root, compiler) => {
    await compiler.compile(root, "theme.css");
    await rm(join(root, "parts", "a.css"));
    await assert.rejects(
      compiler.compile(root, "theme.css"),
      (error: unknown) =>
        error instanceof ThemeCompileError && error.code === "THEME_FILE_NOT_FOUND",
    );
    assert.equal(compiler.stats().entries, 0);
  }));

test("replacing the package directory with identical content recompiles", () =>
  withTheme(async (root, compiler, scratch) => {
    const first = await compiler.compile(root, "theme.css");
    await rename(root, join(scratch, "previous"));
    await writeFiles(root, THEME);
    const second = await compiler.compile(root, "theme.css");
    assert.notEqual(second, first);
    assert.equal(second.styleId, first.styleId);
  }));

test("an explicit invalidation recompiles", () =>
  withTheme(async (root, compiler) => {
    const first = await compiler.compile(root, "theme.css");
    await compiler.invalidate(root);
    assert.equal(compiler.stats().entries, 0);
    const second = await compiler.compile(root, "theme.css");
    assert.notEqual(second, first);
    assert.equal(second.styleId, first.styleId);
  }));

test("watch events invalidate the entry", () =>
  withTheme(async (root, compiler) => {
    await compiler.compile(root, "theme.css");
    // 等待监听建立后修改。Change the file once the watchers are set up.
    await delay(200);
    await writeFile(join(root, "parts", "a.css"), ".a { color: purple; }");
    const deadline = Date.now() + 5_000;
    while (compiler.stats().entries > 0 && Date.now() < deadline) await delay(50);
    assert.equal(compiler.stats().entries, 0);
  }));

test("the LRU releases unused entries and keeps the pinned theme", async () => {
  const scratch = await createScratchDirectory("theme-cache");
  const compiler = new ThemeStyleCompiler(100);
  try {
    const one = join(scratch.path, "one");
    const two = join(scratch.path, "two");
    await writeFiles(one, { "theme.css": `.one { content: "${"x".repeat(60)}"; }` });
    await writeFiles(two, { "theme.css": `.two { content: "${"y".repeat(60)}"; }` });
    const first = await compiler.compile(one, "theme.css");
    await compiler.compile(two, "theme.css");
    assert.equal(compiler.stats().entries, 1);
    assert.notEqual(await compiler.compile(one, "theme.css"), first);

    await compiler.pin(one, "theme.css");
    const pinned = await compiler.compile(one, "theme.css");
    await compiler.compile(two, "theme.css");
    assert.equal(await compiler.compile(one, "theme.css"), pinned);
  } finally {
    await compiler.invalidate(join(scratch.path, "one"));
    await compiler.invalidate(join(scratch.path, "two"));
    await scratch.dispose();
  }
});
