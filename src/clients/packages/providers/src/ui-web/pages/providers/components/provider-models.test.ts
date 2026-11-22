import assert from "node:assert/strict";
import test from "node:test";

test("mergeProviderModelOptions marks only remote-only exact names as new", async () => {
  const { mergeProviderModelOptions } = await import("./provider-models" + ".ts");
  const result = mergeProviderModelOptions(
    [
      { id: "model-1", name: "gpt-4o" },
      { id: "model-2", name: "local-model" },
    ],
    [" gpt-4o ", "GPT-4O", "new-model", "new-model", ""],
  );

  assert.deepEqual(result, [
    { id: null, name: "GPT-4O", isNew: true },
    { id: "model-1", name: "gpt-4o", isNew: false },
    { id: "model-2", name: "local-model", isNew: false },
    { id: null, name: "new-model", isNew: true },
  ]);
});

test("findDiscoveryApiKey returns the first enabled non-empty ApiKey", async () => {
  const { findDiscoveryApiKey } = await import("./provider-models" + ".ts");
  const apiKey = findDiscoveryApiKey([
    { authType: "ApiKey", apiKey: "  ", envKey: null, enable: true },
    { authType: "ApiKey", apiKey: " first-key ", envKey: null, enable: true },
    { authType: "ApiKey", apiKey: "second-key", envKey: null, enable: true },
  ]);

  assert.equal(apiKey, "first-key");
});

test("findDiscoveryApiKey ignores disabled ApiKey configs", async () => {
  const { findDiscoveryApiKey } = await import("./provider-models" + ".ts");
  const apiKey = findDiscoveryApiKey([
    { authType: "ApiKey", apiKey: "disabled-key", envKey: null, enable: false },
  ]);

  assert.equal(apiKey, null);
});

test("model discovery supports OpenAI and Ollama provider types", async () => {
  const { isProviderModelDiscoverySupported } = await import("./provider-models" + ".ts");
  assert.equal(isProviderModelDiscoverySupported("OpenAIChatCompletions"), true);
  assert.equal(isProviderModelDiscoverySupported("OpenAIResponses"), true);
  assert.equal(isProviderModelDiscoverySupported("OllamaNative"), true);
  assert.equal(isProviderModelDiscoverySupported("Anthropic"), false);
});

test("model discovery requires an ApiKey only for OpenAI provider types", async () => {
  const { isProviderModelDiscoveryApiKeyRequired } = await import("./provider-models" + ".ts");
  assert.equal(isProviderModelDiscoveryApiKeyRequired("OpenAIChatCompletions"), true);
  assert.equal(isProviderModelDiscoveryApiKeyRequired("OpenAIResponses"), true);
  assert.equal(isProviderModelDiscoveryApiKeyRequired("OllamaNative"), false);
});

test("mergeProviderModelOptions shows measured Ollama context windows", async () => {
  const { mergeProviderModelOptions } = await import("./provider-models" + ".ts");
  const result = mergeProviderModelOptions(
    [{ id: "model-1", name: "existing-model" }],
    ["existing-model", "new-model"],
    { "existing-model": 16_384, "new-model": 32_768 },
  );

  assert.deepEqual(result, [
    { id: "model-1", name: "existing-model", isNew: false, contextWindowTokens: 16_384 },
    { id: null, name: "new-model", isNew: true, contextWindowTokens: 32_768 },
  ]);
});

test("selected Ollama measurements include only selected new models", async () => {
  const { getSelectedOllamaContextWindows } = await import("./provider-models" + ".ts");
  const result = getSelectedOllamaContextWindows(
    "OllamaNative",
    ["new-model", "existing-model", "unmeasured-model"],
    [{ id: "model-1", name: "existing-model" }],
    { "new-model": 32_768, "existing-model": 16_384, "unselected-model": 8_192 },
  );

  assert.deepEqual(result, { "new-model": 32_768 });
});

test("selected measurements are omitted for non-Ollama providers and empty results", async () => {
  const { getSelectedOllamaContextWindows } = await import("./provider-models" + ".ts");
  assert.equal(
    getSelectedOllamaContextWindows("OpenAIResponses", ["new-model"], [], { "new-model": 32_768 }),
    undefined,
  );
  assert.equal(getSelectedOllamaContextWindows("OllamaNative", ["new-model"], [], {}), undefined);
  assert.equal(
    getSelectedOllamaContextWindows("OllamaNative", ["new-model"], undefined, {
      "new-model": 32_768,
    }),
    undefined,
  );
});

test("discovery refresh keeps model names and uses the latest window measurements", async () => {
  const { getSelectedOllamaContextWindows, mergeProviderModelDiscoveryResults } = await import(
    "./provider-models" + ".ts"
  );
  const result = mergeProviderModelDiscoveryResults(
    {
      modelNames: ["previous-model", "existing-model"],
      ollamaContextWindows: { "previous-model": 16_384, "existing-model": 8_192 },
    },
    {
      modelNames: ["new-model", "previous-model"],
      ollamaContextWindows: { "new-model": 32_768 },
    },
  );

  assert.deepEqual(result, {
    modelNames: ["previous-model", "existing-model", "new-model"],
    ollamaContextWindows: { "new-model": 32_768 },
  });
  assert.deepEqual(
    getSelectedOllamaContextWindows(
      "OllamaNative",
      ["previous-model", "new-model", "existing-model", "unmeasured-model"],
      [{ id: "model-1", name: "existing-model" }],
      result.ollamaContextWindows ?? {},
    ),
    { "new-model": 32_768 },
  );
});
