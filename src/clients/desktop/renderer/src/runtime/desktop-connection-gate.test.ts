import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const GATE_URL = new URL("./desktop-connection-gate.tsx", import.meta.url);

test("Desktop only shows the connection notice for error states", async () => {
  const source = await readFile(GATE_URL, "utf8");

  assert.match(
    source,
    /desktop\.status === "ready" \|\|\s*desktop\.status === "authentication-required"/,
  );
  assert.match(source, /desktop\.status === "loading" \|\| desktop\.status === "setup-required"/);
  assert.doesNotMatch(source, /Add an API token for this Server in Settings\./);
  assert.match(source, /desktop\.status === "incompatible"/);
  assert.match(source, /desktop\.error \|\| "Agw Server is unavailable\."/);
});
