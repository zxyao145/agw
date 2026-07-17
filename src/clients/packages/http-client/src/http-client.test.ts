import assert from "node:assert/strict";
import test from "node:test";

import { appendQuery, compilePath, readResponseBody, unwrapApiResultEnvelope } from "./index.ts";

test("compilePath encodes values and rejects missing parameters", () => {
  assert.equal(compilePath("/api/items/{id}", { id: "a/b" }), "/api/items/a%2Fb");
  assert.throws(() => compilePath("/api/items/{id}", {}), /Missing path param: id/u);
});

test("appendQuery repeats arrays and ignores absent values", () => {
  assert.equal(
    appendQuery("/api/items", { tag: ["one", "two"], empty: null, page: 2 }),
    "/api/items?tag=one&tag=two&page=2",
  );
});

test("response helpers parse and unwrap Bens.Results envelopes", async () => {
  const response = new Response(JSON.stringify({ code: 0, title: "OK", data: { id: "item-1" } }), {
    headers: { "content-type": "application/json" },
  });

  assert.deepEqual(unwrapApiResultEnvelope(await readResponseBody(response)), { id: "item-1" });
});
