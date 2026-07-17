import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const MAIN_URL = new URL("./index.ts", import.meta.url);

test("Desktop starts and reopens on the dedicated Chat route", async () => {
  const source = await readFile(MAIN_URL, "utf8");

  assert.match(source, /loadRenderer\(pathname = "\/desktop\/chat\/"\)/);
  assert.match(source, /Open Agw Chat"[\s\S]*?showWindow\("\/desktop\/chat\/"\)/);
  assert.match(source, /tray\.on\("click", \(\) => void showWindow\("\/desktop\/chat\/"\)\)/);
  assert.doesNotMatch(source, /showWindow\("\/chat\/"\)/);
});
