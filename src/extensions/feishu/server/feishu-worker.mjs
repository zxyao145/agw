import { createHash } from "node:crypto";
import { createInterface } from "node:readline";
import * as Lark from "@larksuiteoapi/node-sdk";
import { createCallbackChannel, createEventDispatcher } from "./feishu-event-dispatcher.mjs";
import { createWebhookPayload } from "./feishu-webhook.mjs";

const logger = {
  error() {
    process.stderr.write("Feishu SDK operation failed\n");
  },
};

const jsonOptions = { loggerLevel: Lark.LoggerLevel.error, logger };
let sessionMode = "one-shot";
const clients = new Map();
let activeCallbackChannel;

function send(value) {
  process.stdout.write(`${JSON.stringify(value)}\n`);
}

function createClient(app) {
  return new Lark.Client({
    appId: app.appId,
    appSecret: app.appSecret,
    appType: Lark.AppType.SelfBuild,
    domain: app.openDomain,
    ...jsonOptions,
  });
}

function getClient(app) {
  const cacheKey = `${app.openDomain}\0${app.appId}`;
  const credentialHash = createHash("sha256").update(app.appSecret).digest("hex");
  const cached = clients.get(cacheKey);
  if (cached?.credentialHash === credentialHash) return cached.client;
  const client = createClient(app);
  clients.set(cacheKey, { credentialHash, client });
  return client;
}

function makeUuid(value) {
  const bytes = createHash("sha256").update(value).digest().subarray(0, 16);
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const hex = bytes.toString("hex");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

async function request(client, method, url, data, params) {
  const response = await client.request({ method, url, data, params });
  if (response.code !== 0) {
    throw new Error("Feishu API rejected the request.");
  }
  return response;
}

async function verify({ app }) {
  const client = getClient(app);
  try {
    await request(client, "POST", "/open-apis/auth/v3/tenant_access_token/internal", {
      app_id: app.appId,
      app_secret: app.appSecret,
    });
    return { valid: true };
  } catch {
    return { valid: false, error: "Feishu rejected the app credentials." };
  }
}

async function discover({ app }) {
  const client = getClient(app);
  const result = [];
  let pageToken;
  for (let page = 0; page < 20; page++) {
    const params = { page_size: 100 };
    if (pageToken) params.page_token = pageToken;
    const response = await request(client, "GET", "/open-apis/im/v1/chats", undefined, params);
    for (const item of response.data?.items ?? []) {
      result.push({ receiveId: item.chat_id, name: item.name ?? item.chat_id });
    }
    if (!response.data?.has_more) return result;
    pageToken = response.data.page_token;
    if (!pageToken) throw new Error("Feishu returned an invalid page token.");
  }
  return result;
}

async function sendApplicationMessage({ app, receiveId, receiveIdType, idempotencyKey, content }) {
  const client = getClient(app);
  const response = await request(
    client,
    "POST",
    "/open-apis/im/v1/messages",
    {
      receive_id: receiveId,
      msg_type: "interactive",
      content: JSON.stringify(content),
      uuid: makeUuid(idempotencyKey),
    },
    { receive_id_type: receiveIdType },
  );
  return { messageId: response.data.message_id };
}

async function sendText({ app, receiveId, receiveIdType, text, idempotencyKey }) {
  const client = getClient(app);
  const response = await request(
    client,
    "POST",
    "/open-apis/im/v1/messages",
    {
      receive_id: receiveId,
      msg_type: "text",
      content: JSON.stringify({ text }),
      uuid: makeUuid(idempotencyKey),
    },
    { receive_id_type: receiveIdType },
  );
  return { messageId: response.data.message_id };
}

async function sendCard({ app, receiveId, receiveIdType, content, idempotencyKey }) {
  const client = getClient(app);
  const response = await request(
    client,
    "POST",
    "/open-apis/im/v1/messages",
    {
      receive_id: receiveId,
      msg_type: "interactive",
      content: JSON.stringify(content),
      uuid: makeUuid(idempotencyKey),
    },
    { receive_id_type: receiveIdType },
  );
  return { messageId: response.data.message_id };
}

async function update({ app, messageId, content }) {
  const client = getClient(app);
  await request(client, "PATCH", `/open-apis/im/v1/messages/${encodeURIComponent(messageId)}`, {
    content: JSON.stringify(content),
  });
  return {};
}

function webhookUrlAllowed(value) {
  const url = new URL(value);
  return (
    url.protocol === "https:" &&
    ["open.feishu.cn", "open.larksuite.com"].includes(url.hostname) &&
    url.port === "" &&
    url.username === "" &&
    url.password === "" &&
    url.search === "" &&
    url.hash === "" &&
    url.pathname.startsWith("/open-apis/bot/v2/hook/")
  );
}

async function sendWebhook({ webhookUrl, webhookSecret, message, dispatchKey }) {
  if (!webhookUrlAllowed(webhookUrl)) throw new Error("The Feishu webhook URL is invalid.");
  const payload = createWebhookPayload(message, webhookSecret);
  const response = await fetch(webhookUrl, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(payload),
    redirect: "error",
    signal: AbortSignal.timeout(10000),
  });
  if (!response.ok) throw new Error("The Feishu webhook request failed.");
  const result = await response.json();
  if (result.code !== 0) throw new Error("The Feishu webhook rejected the message.");
  return { messageId: `webhook:${dispatchKey}` };
}

function startWebSocket({ appRecordId, app }) {
  sessionMode = "websocket";
  const callbackChannel = createCallbackChannel(send);
  const eventDispatcher = createEventDispatcher({
    appRecordId,
    dispatcherOptions: jsonOptions,
    sendEvent: ({ appRecordId: recordId, eventKey, payload }) => {
      send({ type: "event", eventKey, payload, appRecordId: recordId });
    },
    requestCardAction: callbackChannel.request,
  });
  const sendConnectionStatus = (state) => {
    send({ type: "status", state });
  };
  const wsClient = new Lark.WSClient({
    appId: app.appId,
    appSecret: app.appSecret,
    domain: app.openDomain,
    ...jsonOptions,
    onReady: () => sendConnectionStatus("connected"),
    onError: () => {
      sendConnectionStatus("failed");
      send({ type: "error" });
    },
    onReconnecting: () => sendConnectionStatus("reconnecting"),
    onReconnected: () => sendConnectionStatus("connected"),
  });
  void wsClient.start({ eventDispatcher }).catch(() => {
    sendConnectionStatus("failed");
    send({ type: "error" });
  });
  const state = wsClient.getConnectionStatus().state;
  sendConnectionStatus(state === "idle" ? "failed" : state);
  send({ type: "ready" });
  return callbackChannel;
}

const operations = {
  verify,
  discover,
  send: sendApplicationMessage,
  sendText,
  sendCard,
  update,
  webhook: sendWebhook,
};

const input = createInterface({ input: process.stdin, crlfDelay: Infinity });
for await (const line of input) {
  const command = JSON.parse(line);
  if (command.type === "callbackResult") {
    activeCallbackChannel.resolve(command);
    continue;
  }
  if (command.operation === "startDaemon") {
    sessionMode = "daemon";
    send({ type: "ready" });
    continue;
  }
  if (command.operation === "startWs") {
    activeCallbackChannel = startWebSocket(command);
    continue;
  }
  const operation = operations[command.operation];
  if (!operation) {
    const response = { ok: false, error: "Unsupported operation." };
    if (sessionMode === "daemon")
      send({ type: "result", requestId: command.requestId, ...response });
    else send(response);
    if (sessionMode === "one-shot") process.exitCode = 1;
    if (sessionMode !== "daemon") break;
    continue;
  }
  try {
    const result = await operation(command.payload);
    const response = { ok: true, payload: result };
    if (sessionMode === "daemon")
      send({ type: "result", requestId: command.requestId, ...response });
    else send(response);
  } catch {
    const response = { ok: false, error: "Feishu operation failed." };
    if (sessionMode === "daemon")
      send({ type: "result", requestId: command.requestId, ...response });
    else send(response);
    if (sessionMode === "one-shot") process.exitCode = 1;
  }
  if (sessionMode === "one-shot") break;
}
