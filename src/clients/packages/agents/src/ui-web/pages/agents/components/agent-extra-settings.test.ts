import assert from "node:assert/strict";
import { createRequire } from "node:module";
import test from "node:test";

const require = createRequire(import.meta.url);
const { AGENT_EXTRA_SETTINGS_ERROR, getAgentExtraSettingsError, normalizeAgentExtraSettings } =
  require("./agent-extra-settings.ts") as typeof import("./agent-extra-settings");

test("normalizeAgentExtraSettings returns null for blank input", () => {
  assert.equal(normalizeAgentExtraSettings("   "), null);
});

test("normalizeAgentExtraSettings trims and preserves a JSON object", () => {
  assert.equal(
    normalizeAgentExtraSettings('  {"sandbox": false, "env": {}}  '),
    '{"sandbox": false, "env": {}}',
  );
});

test("getAgentExtraSettingsError rejects invalid and non-object JSON", () => {
  for (const value of ["not-json", "[]", '"text"', "42", "true", "null"]) {
    assert.equal(getAgentExtraSettingsError(value), AGENT_EXTRA_SETTINGS_ERROR);
  }
});

test("normalizeAgentExtraSettings throws for invalid JSON", () => {
  assert.throws(() => normalizeAgentExtraSettings("[]"), new Error(AGENT_EXTRA_SETTINGS_ERROR));
});
