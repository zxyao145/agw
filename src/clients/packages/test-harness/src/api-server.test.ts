import assert from "node:assert/strict";
import test from "node:test";

import { rawApiResponse, startApiServer, type ApiRequestRecord } from "./api-server";

test("payload routes keep the Bens.Results envelope", async () => {
  const api = await startApiServer({ "GET /api/files/stat": { path: "a.md" } });

  const response = await fetch(`${api.baseUrl}/api/files/stat?projectId=p`);

  assert.equal(response.status, 200);
  assert.deepEqual(await response.json(), { code: 0, title: "OK", data: { path: "a.md" } });
  assert.equal(api.requests[0]?.query.get("projectId"), "p");
});

test("raw responses are written with their own status, headers and string body", async () => {
  const api = await startApiServer({
    "DELETE /api/files/delete": (request: ApiRequestRecord) =>
      rawApiResponse({
        status: 409,
        headers: { "content-type": "application/json" },
        body: JSON.stringify({
          code: 4090023,
          title: `Directory '${request.query.get("path")}' is not empty.`,
        }),
      }),
  });

  const response = await fetch(`${api.baseUrl}/api/files/delete?path=docs&recursive=false`, {
    method: "DELETE",
  });

  assert.equal(response.status, 409);
  assert.equal(response.headers.get("content-type"), "application/json");
  assert.deepEqual(await response.json(), {
    code: 4090023,
    title: "Directory 'docs' is not empty.",
  });
});

test("raw responses carry binary bodies unchanged", async () => {
  const api = await startApiServer({
    "GET /api/files/download": rawApiResponse({
      headers: { "content-type": "application/octet-stream" },
      body: new Uint8Array([0x00, 0x80, 0xff]),
    }),
  });

  const response = await fetch(`${api.baseUrl}/api/files/download?path=logo.png`);

  assert.equal(response.status, 200);
  assert.equal(response.headers.get("content-type"), "application/octet-stream");
  assert.deepEqual([...new Uint8Array(await response.arrayBuffer())], [0x00, 0x80, 0xff]);
});
