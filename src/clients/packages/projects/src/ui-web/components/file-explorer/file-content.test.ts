import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const FILE_CONTENT_URL = new URL("./file-content.tsx", import.meta.url);

test("file content fills the available preview panel width", async () => {
  const source = await readFile(FILE_CONTENT_URL, "utf8");
  const rootClassName = source.match(/return \(\s*<div className="([^"]+)"/)?.[1];

  assert.ok(rootClassName);
  const classes = rootClassName.split(" ");
  assert.ok(classes.includes("w-full"));
  assert.ok(classes.includes("min-w-0"));
});
