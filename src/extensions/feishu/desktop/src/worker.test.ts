import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { createServer } from "node:http";
import { createInterface } from "node:readline";
import { createHmac } from "node:crypto";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { createWebhookPayload } from "../../server/feishu-webhook.mjs";

test("worker keeps its generation session alive and rejects private webhook URLs", async (context) => {
  const packageRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
  const bundledWorkerPath = resolve(packageRoot, "dist/server/feishu-worker.mjs");
  const workerPath = existsSync(bundledWorkerPath)
    ? bundledWorkerPath
    : resolve(packageRoot, "server/feishu-worker.mjs");
  const worker = spawn(process.execPath, [workerPath], {
    cwd: packageRoot,
    stdio: ["pipe", "pipe", "inherit"],
  });
  const lines = createInterface({ input: worker.stdout });
  const output = lines[Symbol.asyncIterator]();
  context.after(() => {
    lines.close();
    if (worker.exitCode === null) worker.kill();
  });

  worker.stdin.write(`${JSON.stringify({ operation: "startDaemon" })}\n`);
  assert.deepEqual(JSON.parse((await output.next()).value!), { type: "ready" });

  const command = {
    operation: "webhook",
    payload: {
      webhookUrl: "https://127.0.0.1/open-apis/bot/v2/hook/private",
      webhookSecret: "",
      message: { content: {} },
      dispatchKey: "test",
    },
  };
  worker.stdin.write(`${JSON.stringify({ ...command, requestId: "first" })}\n`);
  const first = JSON.parse((await output.next()).value!);
  assert.equal(first.requestId, "first");
  assert.equal(first.ok, false);

  worker.stdin.write(`${JSON.stringify({ ...command, requestId: "second" })}\n`);
  const second = JSON.parse((await output.next()).value!);
  assert.equal(second.requestId, "second");
  assert.equal(second.ok, false);
  assert.equal(worker.exitCode, null);
});

test("worker keeps its WebSocket process alive after announcing readiness", async (context) => {
  const packageRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
  const bundledWorkerPath = resolve(packageRoot, "dist/server/feishu-worker.mjs");
  const workerPath = existsSync(bundledWorkerPath)
    ? bundledWorkerPath
    : resolve(packageRoot, "server/feishu-worker.mjs");
  const worker = spawn(process.execPath, [workerPath], {
    cwd: packageRoot,
    stdio: ["pipe", "pipe", "inherit"],
  });
  const lines = createInterface({ input: worker.stdout });
  const output = lines[Symbol.asyncIterator]();
  context.after(() => {
    lines.close();
    if (worker.exitCode === null) worker.kill();
  });

  worker.stdin.write(
    `${JSON.stringify({
      operation: "startWs",
      appRecordId: "019a4c17-5f00-7a80-8b3d-2b732f48f112",
      app: {
        appId: "cli_0123456789abcdef",
        appSecret: "local-test-secret",
        openDomain: "http://127.0.0.1:1",
      },
    })}\n`,
  );
  const states: string[] = [];
  let ready = false;
  while (!ready) {
    const message = JSON.parse((await output.next()).value!);
    if (message.type === "status") states.push(message.state);
    if (message.type === "error")
      assert.fail("The local WS worker failed before startup readiness.");
    ready = message.type === "ready";
  }
  assert.ok(states.includes("connecting"));
  assert.ok(!states.includes("connected"));
  await new Promise((resolvePromise) => setTimeout(resolvePromise, 100));
  assert.equal(worker.exitCode, null);
});

test("worker reports a real SDK failure from a local WebSocket endpoint", async (context) => {
  const requests: string[] = [];
  const server = createServer((request, response) => {
    requests.push(request.url ?? "");
    response.writeHead(200, { "content-type": "application/json" });
    response.end(JSON.stringify({ code: 10003, msg: "invalid local endpoint", data: {} }));
  });
  await new Promise<void>((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
  const address = server.address();
  assert.ok(address && typeof address === "object");

  const packageRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
  const bundledWorkerPath = resolve(packageRoot, "dist/server/feishu-worker.mjs");
  const workerPath = existsSync(bundledWorkerPath)
    ? bundledWorkerPath
    : resolve(packageRoot, "server/feishu-worker.mjs");
  const worker = spawn(process.execPath, [workerPath], {
    cwd: packageRoot,
    stdio: ["pipe", "pipe", "inherit"],
  });
  const lines = createInterface({ input: worker.stdout });
  const output = lines[Symbol.asyncIterator]();
  context.after(async () => {
    lines.close();
    if (worker.exitCode === null) worker.kill();
    await new Promise<void>((resolveClose, rejectClose) => {
      server.close((error) => (error ? rejectClose(error) : resolveClose()));
    });
  });

  worker.stdin.write(
    `${JSON.stringify({
      operation: "startWs",
      appRecordId: "019a4c17-5f00-7a80-8b3d-2b732f48f112",
      app: {
        appId: "cli_0123456789abcdef",
        appSecret: "local-test-secret",
        openDomain: `http://127.0.0.1:${address.port}`,
      },
    })}\n`,
  );

  const states: string[] = [];
  let ready = false;
  let failed = false;
  while (!failed) {
    const line = await output.next();
    assert.ok(!line.done);
    const message = JSON.parse(line.value);
    if (message.type === "status") states.push(message.state);
    ready ||= message.type === "ready";
    failed = message.type === "error";
  }

  assert.equal(ready, true);
  assert.ok(requests.some((path) => path.endsWith("/callback/ws/endpoint")));
  assert.ok(states.includes("connecting"));
  assert.ok(states.includes("failed"));
  assert.ok(!states.includes("connected"));
});

test("webhook payload signs the serialized Feishu message without mutating it", () => {
  const message = {
    msg_type: "interactive",
    card: { elements: [{ tag: "div", text: { content: "Agw message" } }] },
  };
  const timestamp = "1700000000";
  const webhookSecret = "test-signing-secret";
  const sign = createHmac("sha256", `${timestamp}\n${webhookSecret}`).update("").digest("base64");

  assert.deepEqual(createWebhookPayload(message, webhookSecret, 1_700_000_000_000), {
    ...message,
    timestamp,
    sign,
  });
  assert.deepEqual(message, {
    msg_type: "interactive",
    card: { elements: [{ tag: "div", text: { content: "Agw message" } }] },
  });
});
