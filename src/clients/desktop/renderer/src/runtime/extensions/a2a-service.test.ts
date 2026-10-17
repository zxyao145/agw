import assert from "node:assert/strict";
import test from "node:test";

import { configureApiRuntime } from "@agw/api";
import { rawApiResponse, startApiServer } from "@agw/test-harness";
import { A2AService } from "./a2a-service";
import { ContextService } from "./context-service";

function json(value: unknown) {
  return rawApiResponse({
    headers: { "content-type": "application/json" },
    body: JSON.stringify(value),
  });
}

async function createService(routes: Parameters<typeof startApiServer>[0]) {
  const api = await startApiServer(routes);
  const context = new ContextService(
    { version: "0.5.0", platform: "darwin", architecture: "arm64" },
    {
      sidebarVisible: true,
      activeSidebarViewId: "agw.conversations",
      activeMainViewId: "agw.chat",
    },
  );
  configureApiRuntime({ baseUrl: api.baseUrl, token: "agw_secret" });
  context.setConnection(
    { profileId: "local", baseUrl: api.baseUrl, token: "agw_secret" },
    {
      id: "local",
      name: "Local",
      kind: "local",
    },
  );
  return { api, service: new A2AService(context) };
}

test("agents and agent cards are read with the Bearer token", async () => {
  const { api, service } = await createService({
    "GET /.well-known/agents.json": json([{ name: "echo" }]),
    "GET /a2a/echo/.well-known/agent-card.json": json({ name: "echo", version: "1.0.0" }),
  });
  assert.deepEqual(await service.listAgents(), [{ name: "echo" }]);
  assert.deepEqual(await service.getAgentCard("echo"), { name: "echo", version: "1.0.0" });
  for (const request of api.requests) {
    assert.equal(request.headers.authorization, "Bearer agw_secret");
    assert.equal(request.headers["a2a-version"], "1.0");
  }
});

test("requests send JSON-RPC 2.0 and return the result", async () => {
  const { api, service } = await createService({
    "POST /a2a/echo": (request: { body: unknown }) =>
      json({
        jsonrpc: "2.0",
        id: (request.body as { id: string }).id,
        result: { kind: "message", text: "hi" },
      }),
  });
  assert.deepEqual(await service.request("echo", "SendMessage", { message: { text: "hi" } }), {
    kind: "message",
    text: "hi",
  });
  const sent = api.requests.at(-1)!;
  assert.equal(sent.headers["content-type"], "application/json");
  assert.equal(sent.headers["a2a-version"], "1.0");
  assert.equal(sent.headers.authorization, "Bearer agw_secret");
  const body = sent.body as { jsonrpc: string; id: string; method: string; params: unknown };
  assert.equal(body.jsonrpc, "2.0");
  assert.equal(body.method, "SendMessage");
  assert.deepEqual(body.params, { message: { text: "hi" } });
  assert.equal(typeof body.id, "string");
});

test("JSON-RPC errors become ERR_A2A_RPC", async () => {
  const { service } = await createService({
    "POST /a2a/echo": json({
      jsonrpc: "2.0",
      id: "1",
      error: { code: -32001, message: "Task not found" },
    }),
  });
  await assert.rejects(service.request("echo", "GetTask", { id: "missing" }), {
    code: "ERR_A2A_RPC",
    message: "-32001: Task not found",
  });
});

test("streams parse every SSE event and finish with the stream", async () => {
  const { api, service } = await createService({
    "POST /a2a/echo": rawApiResponse({
      headers: { "content-type": "text/event-stream" },
      body:
        'data: {"jsonrpc":"2.0","id":"1","result":{"kind":"status-update","state":"working"}}\n\n' +
        'data: {"jsonrpc":"2.0","id":"1","result":{"kind":"message","text":"done"}}\n\n',
    }),
  });
  const events: unknown[] = [];
  await service.stream("echo", "SendStreamingMessage", { message: { text: "hi" } }, (event) =>
    events.push(event),
  );
  assert.deepEqual(events, [
    { jsonrpc: "2.0", id: "1", result: { kind: "status-update", state: "working" } },
    { jsonrpc: "2.0", id: "1", result: { kind: "message", text: "done" } },
  ]);
  assert.equal(api.requests.at(-1)!.headers.accept, "text/event-stream");
});

test("push notification configuration methods are refused without sending", async () => {
  const { api, service } = await createService({});
  const sent = api.requests.length;
  for (const method of [
    "CreateTaskPushNotificationConfig",
    "GetTaskPushNotificationConfig",
    "ListTaskPushNotificationConfig",
    "DeleteTaskPushNotificationConfig",
  ]) {
    assert.throws(() => service.request("echo", method, {}), { code: "ERR_UNSUPPORTED_OPERATION" });
  }
  assert.equal(api.requests.length, sent);
});
