import assert from "node:assert/strict";
import test from "node:test";

test("listKeysByPair attaches X-API-Key header from unified API client", async (t) => {
  const { setApiKey } = await import("../../../../../api/client" + ".ts");
  const { listKeysByPair } = await import("./utils" + ".ts");
  const originalFetch = globalThis.fetch;
  const requests: Array<{ url: string; init?: RequestInit }> = [];

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    requests.push({ url: String(input), init });
    return new Response(JSON.stringify([]), {
      status: 200,
      headers: { "content-type": "application/json" },
    });
  }) as typeof fetch;

  setApiKey("model-provider-key-123");

  t.after(() => {
    globalThis.fetch = originalFetch;
    setApiKey(null);
  });

  await listKeysByPair({ modelProviderId: "mp-1" });

  const headers = requests[0].init?.headers as Record<string, string> | undefined;
  assert.equal(headers?.["X-API-Key"], "model-provider-key-123");
  assert.equal(requests[0].url, "/api/model-provider-keys?modelProviderId=mp-1");
});
