import assert from "node:assert/strict";
import { createRequire } from "node:module";
import test from "node:test";

const require = createRequire(import.meta.url);
const {
  getEnvironmentVariablesError: getAgentEnvironmentVariablesError,
  normalizeEnvironmentVariables: normalizeAgentEnvironmentVariables,
  toEnvironmentVariableEntries: toAgentEnvironmentVariableEntries,
} = require("../../../../../../integrations/src/ui-web/components/definition-capabilities/environment-variables.ts") as typeof import("../../../../../../integrations/src/ui-web/components/definition-capabilities/environment-variables");

test("normalizeAgentEnvironmentVariables trims keys and preserves empty values", () => {
  const result = normalizeAgentEnvironmentVariables([
    { key: " AGW_TOKEN ", value: "secret" },
    { key: "EMPTY_VALUE", value: "" },
  ]);

  assert.deepEqual(result, {
    AGW_TOKEN: "secret",
    EMPTY_VALUE: "",
  });
});

test("getAgentEnvironmentVariablesError rejects blank and invalid keys", () => {
  assert.equal(
    getAgentEnvironmentVariablesError([{ key: "   ", value: "value" }]),
    "Environment variable key is required.",
  );
  assert.equal(
    getAgentEnvironmentVariablesError([{ key: "INVALID=NAME", value: "value" }]),
    "Environment variable key cannot contain '=' or a null character.",
  );
  assert.equal(
    getAgentEnvironmentVariablesError([{ key: "INVALID\0NAME", value: "value" }]),
    "Environment variable key cannot contain '=' or a null character.",
  );
});

test("getAgentEnvironmentVariablesError rejects duplicate trimmed keys", () => {
  const error = getAgentEnvironmentVariablesError([
    { key: "SHARED", value: "first" },
    { key: " SHARED ", value: "second" },
  ]);

  assert.equal(error, "Environment variable keys must be unique.");
});

test("normalizeAgentEnvironmentVariables throws when entries are invalid", () => {
  assert.throws(
    () => normalizeAgentEnvironmentVariables([{ key: "", value: "value" }]),
    /Environment variable key is required/,
  );
});

test("toAgentEnvironmentVariableEntries converts a response record to editable rows", () => {
  const result = toAgentEnvironmentVariableEntries({
    AGW_TOKEN: "secret",
    EMPTY_VALUE: "",
  });

  assert.deepEqual(result, [
    { key: "AGW_TOKEN", value: "secret" },
    { key: "EMPTY_VALUE", value: "" },
  ]);
  assert.deepEqual(toAgentEnvironmentVariableEntries(null), []);
});
