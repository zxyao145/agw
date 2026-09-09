import assert from "node:assert/strict";
import test from "node:test";
import { getExternalModelProviderError } from "./external-model-provider";
import { ExternalAgentKind, type ExternalAgentOptionDto, type ModelProviderDto } from "./types";

const options: ExternalAgentOptionDto[] = [
  {
    kind: ExternalAgentKind.ClaudeCode,
    displayName: "Claude Code",
    defaultExtra: "{}",
    supportedProviderTypes: ["Anthropic"],
  },
  {
    kind: ExternalAgentKind.Codex,
    displayName: "Codex",
    defaultExtra: "{}",
    supportedProviderTypes: ["OpenAIResponses"],
  },
  {
    kind: ExternalAgentKind.Pi,
    displayName: "Pi",
    defaultExtra: "{}",
    supportedProviderTypes: ["Anthropic", "OpenAIResponses", "OpenAIChatCompletions"],
  },
];
const models: ModelProviderDto[] = ["Anthropic", "OpenAIResponses", "OpenAIChatCompletions"].map(
  (providerType) => ({
    id: providerType,
    providerType,
    providerId: "provider",
    modelId: "model",
    providerName: "Provider",
    modelName: "Model",
  }),
);

test("compatibility follows the server catalog for all external kinds", () => {
  for (const option of options) {
    for (const model of models) {
      assert.equal(
        getExternalModelProviderError(option.kind, model.id, models, options) === null,
        option.supportedProviderTypes.includes(model.providerType),
      );
    }
  }
});

test("clearing a selection is valid even while options are unavailable", () => {
  for (const option of options) {
    assert.equal(getExternalModelProviderError(option.kind, "", undefined, undefined), null);
    assert.equal(getExternalModelProviderError(option.kind, "", [], options), null);
  }
});

test("missing, loading, and incompatible selections are explicitly rejected", () => {
  assert.match(
    getExternalModelProviderError(ExternalAgentKind.Pi, "deleted", models, options)!,
    /unavailable/,
  );
  assert.match(
    getExternalModelProviderError(ExternalAgentKind.Pi, "Anthropic", undefined, options)!,
    /Loading/,
  );
  assert.match(
    getExternalModelProviderError(ExternalAgentKind.Codex, "Anthropic", models, options)!,
    /incompatible/,
  );
});

test("switching kind rejects only incompatible selections without modifying the catalog", () => {
  assert.equal(
    getExternalModelProviderError(ExternalAgentKind.Pi, "Anthropic", models, options),
    null,
  );
  assert.equal(
    getExternalModelProviderError(ExternalAgentKind.ClaudeCode, "Anthropic", models, options),
    null,
  );
  assert.match(
    getExternalModelProviderError(ExternalAgentKind.Codex, "Anthropic", models, options)!,
    /incompatible/,
  );
  assert.equal(models.length, 3);
});
