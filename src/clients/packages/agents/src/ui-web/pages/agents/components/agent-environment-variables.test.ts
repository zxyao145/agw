import assert from "node:assert/strict";
import test from "node:test";

import {
  getEnvironmentVariablesError as getAgentEnvironmentVariablesError,
  normalizeEnvironmentVariables as normalizeAgentEnvironmentVariables,
  toEnvironmentVariableEntries as toAgentEnvironmentVariableEntries,
} from "@agw/integrations";

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
