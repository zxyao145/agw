import assert from "node:assert/strict";
import test from "node:test";

test("apiGet unwraps Bens.Results data envelopes", async (t) => {
  const { apiGet } = await import("./client" + ".ts");
  const originalFetch = globalThis.fetch;

  globalThis.fetch = (async () =>
    new Response(
      JSON.stringify({
        code: 0,
        title: "OK",
        data: [{ id: "agent-1", name: "agent" }],
      }),
      {
        status: 200,
        headers: { "content-type": "application/json" },
      },
    )) as typeof fetch;

  t.after(() => {
    globalThis.fetch = originalFetch;
  });

  const result = await apiGet("/api/agents");

  assert.deepEqual(result, [{ id: "agent-1", name: "agent" }]);
});
