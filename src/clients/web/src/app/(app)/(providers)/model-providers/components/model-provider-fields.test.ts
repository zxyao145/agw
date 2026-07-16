import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const CREATE_DIALOG_URL = new URL("./create-model-provider-dialog.tsx", import.meta.url);
const TABLE_URL = new URL("./model-provider-table.tsx", import.meta.url);

test("Model Provider create dialog selects only model and provider", async () => {
  const source = await readFile(CREATE_DIALOG_URL, "utf8");

  assert.doesNotMatch(source, /Input price/);
  assert.doesNotMatch(source, /Output price/);
  assert.doesNotMatch(source, /Cache read/);
  assert.doesNotMatch(source, /Cache write/);
  assert.doesNotMatch(source, /RPS limit/);
  assert.match(source, /inputPrice: 0/);
  assert.match(source, /outputPrice: 0/);
  assert.match(source, /cacheRead: 0/);
  assert.match(source, /cacheWrite: 0/);
  assert.match(source, /rpsLimit: 0/);
});

test("Model Provider table shows only provider, model, and actions", async () => {
  const source = await readFile(TABLE_URL, "utf8");

  for (const heading of ["Input", "Output", "Cache read", "Cache write", "RPS"]) {
    assert.doesNotMatch(source, new RegExp(`<TableHead[^>]*>${heading}</TableHead>`));
  }
  assert.match(source, /<TableHead>Provider<\/TableHead>/);
  assert.match(source, /<TableHead>Model<\/TableHead>/);
  assert.match(source, /<TableHead className="text-right">Actions<\/TableHead>/);
});
