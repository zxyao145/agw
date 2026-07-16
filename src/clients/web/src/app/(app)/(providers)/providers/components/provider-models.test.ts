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
    { authType: "EnvVariable", apiKey: null, envKey: "OPENAI_API_KEY", enable: true },
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

test("model discovery supports only OpenAI provider types", async () => {
  const { isProviderModelDiscoverySupported } = await import("./provider-models" + ".ts");
  assert.equal(isProviderModelDiscoverySupported("OpenAIChatCompletions"), true);
  assert.equal(isProviderModelDiscoverySupported("OpenAIResponses"), true);
  assert.equal(isProviderModelDiscoverySupported("Anthropic"), false);
});
