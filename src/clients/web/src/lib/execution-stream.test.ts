import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

test("execution stream contains only transport-neutral helpers", async () => {
  const source = await readFile(new URL("./execution-stream.ts", import.meta.url), "utf8");

  assert.match(source, /export function toExecutionUserInput\(/);
  assert.doesNotMatch(source, /execution-ws|WebSocket|parseExecutionWsMessage/);
});
