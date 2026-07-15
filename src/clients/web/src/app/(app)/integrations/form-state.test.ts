import assert from "node:assert/strict";
import test from "node:test";

import {
  buildFieldPayload,
  createSchemaFormState,
  type SecretFieldFormState,
} from "./form-state.ts";

const fields = [
  { id: "endpoint", label: "Endpoint", type: "Url", isRequired: true },
  { id: "apiKey", label: "API key", type: "Secret", isRequired: true },
] as const;

test("schema form keeps configured secrets and initializes non-secret configuration", () => {
  const form = createSchemaFormState(fields, {
    configuration: { endpoint: "https://example.test" },
    secrets: {
      apiKey: { configured: true },
    },
  });

  assert.equal(form.configuration.endpoint, "https://example.test");
  assert.deepEqual(form.secrets.apiKey, {
    action: "Keep",
    secretValue: "",
  });
});

test("schema form only requests a new value for an unconfigured required secret", () => {
  const form = createSchemaFormState([
    { id: "required", label: "Required", type: "Secret", isRequired: true },
    { id: "optional", label: "Optional", type: "Secret", isRequired: false },
  ]);

  assert.equal(form.secrets.required.action, "Set");
  assert.equal(form.secrets.optional.action, "Keep");
});

test("secret field payload distinguishes keep, encrypted set, and clear", () => {
  const secret = (overrides: Partial<SecretFieldFormState>): SecretFieldFormState => ({
    action: "Keep",
    secretValue: "",
    ...overrides,
  });

  assert.deepEqual(
    buildFieldPayload(fields, { configuration: {}, secrets: { apiKey: secret({}) } }),
    {
      configuration: { endpoint: "" },
      secrets: {
        apiKey: {
          action: "Keep",
          secretValue: null,
        },
      },
    },
  );

  assert.deepEqual(
    buildFieldPayload(fields, {
      configuration: {},
      secrets: { apiKey: secret({ action: "Set", secretValue: "secret" }) },
    }).secrets.apiKey,
    {
      action: "Set",
      secretValue: "secret",
    },
  );

  assert.deepEqual(
    buildFieldPayload(fields, {
      configuration: {},
      secrets: { apiKey: secret({ action: "Clear" }) },
    }).secrets.apiKey,
    {
      action: "Clear",
      secretValue: null,
    },
  );
});
