import assert from "node:assert/strict";
import { createRequire } from "node:module";
import test from "node:test";

const require = createRequire(import.meta.url);
const {
  AGENT_RESPONSE_SCHEMA_ERROR,
  AGENT_RESPONSE_SCHEMA_EXAMPLE,
  getAgentResponseSchemaError,
  normalizeAgentResponseSchema,
} = require("./agent-response-schema.ts") as typeof import("./agent-response-schema");

test("normalizeAgentResponseSchema returns null for blank input", () => {
  assert.equal(normalizeAgentResponseSchema("   "), null);
  assert.equal(normalizeAgentResponseSchema(""), null);
  assert.equal(normalizeAgentResponseSchema("\n\t "), null);
});

test("normalizeAgentResponseSchema trims and preserves a JSON object", () => {
  assert.equal(
    normalizeAgentResponseSchema('  {"type": "object", "properties": {}}  '),
    '{"type": "object", "properties": {}}',
  );
});

test("getAgentResponseSchemaError accepts a schema object", () => {
  assert.equal(getAgentResponseSchemaError(AGENT_RESPONSE_SCHEMA_EXAMPLE), null);
});

test("getAgentResponseSchemaError rejects invalid and non-object JSON", () => {
  for (const value of ["not-json", "{invalid", "[]", '"text"', "42", "true", "null"]) {
    assert.equal(getAgentResponseSchemaError(value), AGENT_RESPONSE_SCHEMA_ERROR);
  }
});

test("normalizeAgentResponseSchema throws for invalid JSON", () => {
  assert.throws(() => normalizeAgentResponseSchema("[]"), new Error(AGENT_RESPONSE_SCHEMA_ERROR));
});
