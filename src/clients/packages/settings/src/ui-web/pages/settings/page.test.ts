import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const PAGE_URL = new URL("./page.tsx", import.meta.url);

test("settings page accepts administrator passwords with at least 8 characters", async () => {
  const source = await readFile(PAGE_URL, "utf8");

  assert.match(source, /minLength=\{8\}/);
  assert.match(source, /disabled=\{newPassword\.length < 8\}/);
});

test("settings page confirms both successful clipboard copies", async () => {
  const source = await readFile(PAGE_URL, "utf8");

  assert.doesNotMatch(source, /from "sonner"/);
  assert.match(source, /toast,\s*\} from "@agw\/components"/);
  assert.match(source, /writeText\(created\.token\);\s*toast\.success\("API token copied"\)/);
  assert.match(
    source,
    /writeText\(encodeBase64Config\(created\.token\)\);\s*toast\.success\("Base64 configuration copied"\)/,
  );
});
