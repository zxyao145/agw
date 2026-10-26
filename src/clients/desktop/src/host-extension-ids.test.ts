import assert from "node:assert/strict";
import { readdir, readFile } from "node:fs/promises";
import { join, relative, resolve } from "node:path";
import test from "node:test";

// 宿主只依赖公开的 manifest 与 Extension API，代码中不出现内置 extension 的 id（集成测试与单元测试除外）。
// The host relies only on the public manifest and Extension API; its code names no bundled extension
// (integration and unit tests excepted).
const EXTENSION_IDS = [
  "01a173b5-526c-74d8-864c-0e2da2d0a21c",
  "01a173b5-526c-74d8-864c-116f66ff919e",
  "01a173b5-526c-74d8-864c-26b0968892f6",
  "01a173b5-526c-74d8-864c-2fa2035185e4",
  "01a173b5-526c-74d8-864c-3fd14f2c28b8",
];

test("host code names no bundled extension", async () => {
  const desktop = process.cwd();
  const matches: string[] = [];
  for (const root of ["src", join("renderer", "src")]) {
    const entries = await readdir(resolve(desktop, root), { recursive: true, withFileTypes: true });
    for (const entry of entries) {
      if (
        !entry.isFile() ||
        entry.name.endsWith(".integration.ts") ||
        entry.name.endsWith(".test.ts")
      )
        continue;
      const path = join(entry.parentPath, entry.name);
      const text = (await readFile(path, "utf8")).toLowerCase();
      for (const id of EXTENSION_IDS) {
        if (text.includes(id)) matches.push(`${relative(desktop, path)}: ${id}`);
      }
    }
  }
  assert.deepEqual(matches, []);
});
