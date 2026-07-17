import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const PAGE_URL = new URL("./page.tsx", import.meta.url);

test("settings page accepts administrator passwords with at least 8 characters", async () => {
  const source = await readFile(PAGE_URL, "utf8");

  assert.match(source, /minLength=\{8\}/);
  assert.match(source, /disabled=\{newPassword\.length < 8\}/);
});
