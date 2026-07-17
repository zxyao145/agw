import assert from "node:assert/strict";
import test from "node:test";

const MODULE_URL = new URL("./environment-variables.ts", import.meta.url);

async function importEnvironmentVariablesModule() {
  try {
    return await import(MODULE_URL.href);
  } catch (error) {
    assert.fail(`shared environment-variable helpers are missing or invalid: ${String(error)}`);
  }
}

test("normalizeEnvironmentVariables trims keys and preserves empty values", async () => {
  const { normalizeEnvironmentVariables } = await importEnvironmentVariablesModule();

  assert.deepEqual(
    normalizeEnvironmentVariables([
      { key: " API_TOKEN ", value: "secret" },
      { key: "EMPTY", value: "" },
    ]),
    { API_TOKEN: "secret", EMPTY: "" },
  );
});

test("getEnvironmentVariablesError rejects blank, equals, null, and duplicate keys", async () => {
  const { getEnvironmentVariablesError } = await importEnvironmentVariablesModule();

  assert.equal(
    getEnvironmentVariablesError([{ key: "  ", value: "value" }]),
    "Environment variable key is required.",
  );
  assert.equal(
    getEnvironmentVariablesError([{ key: "BAD=KEY", value: "value" }]),
    "Environment variable key cannot contain '=' or a null character.",
  );
  assert.equal(
    getEnvironmentVariablesError([{ key: "BAD\0KEY", value: "value" }]),
    "Environment variable key cannot contain '=' or a null character.",
  );
  assert.equal(
    getEnvironmentVariablesError([
      { key: "SHARED", value: "one" },
      { key: " SHARED ", value: "two" },
    ]),
    "Environment variable keys must be unique.",
  );
});

test("toEnvironmentVariableEntries converts response objects into editable rows", async () => {
  const { toEnvironmentVariableEntries } = await importEnvironmentVariablesModule();

  assert.deepEqual(toEnvironmentVariableEntries({ API_TOKEN: "secret", EMPTY: "" }), [
    { key: "API_TOKEN", value: "secret" },
    { key: "EMPTY", value: "" },
  ]);
  assert.deepEqual(toEnvironmentVariableEntries(undefined), []);
});
