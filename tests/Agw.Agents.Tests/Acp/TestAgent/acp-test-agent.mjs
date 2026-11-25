#!/usr/bin/env node
import { randomUUID } from "node:crypto";
import { appendFileSync, existsSync, readFileSync, writeFileSync } from "node:fs";
import { Readable, Writable } from "node:stream";
import { setTimeout as delay } from "node:timers/promises";
import { agent, methods, ndJsonStream, RequestError } from "@agentclientprotocol/sdk";

const [scenarioPath, logPath] = process.argv.slice(2);
const readJson = path => JSON.parse(readFileSync(path, "utf8"));
const scenario = readJson(scenarioPath);
const statePath = logPath + ".state.json";
const state = existsSync(statePath) ? readJson(statePath) : { turn: 0, sessions: [] };
const save = () => writeFileSync(statePath, JSON.stringify(state));
const log = event => appendFileSync(logPath, JSON.stringify(event) + "\n");
log({ event: "start", pid: process.pid, cwd: process.cwd(), argv: process.argv, envNames: Object.keys(process.env) });

const caps = scenario.capabilities ?? {};
const sessionCapabilities = Object.fromEntries(
  ["resume", "close", "additionalDirectories"].filter(key => caps[key]).map(key => [key, {}]),
);
const promptCapabilities = { image: !!caps.image, audio: !!caps.audio, embeddedContext: !!caps.embeddedContext };
const sessionState = () => ({ modes: scenario.modes, configOptions: scenario.configOptions });
let authenticated = !scenario.requireAuth;
let cancel;
const app = agent({ name: "agw-acp-test-agent" });
const method = methods.agent;
const session = method.session;
function request(name, handler) {
  app.onRequest(name, context => {
    log({ method: name, params: context.params });
    if (name === method.initialize && scenario.requireAuthAtInitialize) throw RequestError.authRequired();
    if (!authenticated && name !== method.initialize && name !== method.authenticate) throw RequestError.authRequired();
    return handler(context);
  });
}
request(method.initialize, () => ({
  protocolVersion: scenario.protocolVersion ?? 1,
  agentCapabilities: { loadSession: !!caps.loadSession, promptCapabilities, sessionCapabilities },
  authMethods: scenario.authMethods ?? (scenario.requireAuth ? [{ id: "api-key", name: "API key" }] : []),
  agentInfo: { name: "agw-acp-test-agent", version: "1.0.0" },
}));
request(method.authenticate, () => {
  authenticated = true;
  return {};
});
request(session.new, async ({ signal, client }) => {
  if (scenario.permissionBeforeSession) {
    const { outcome } = await client.request(methods.client.session.requestPermission, scenario.permissionBeforeSession);
    log({ event: "permission", outcome });
  }
  if (scenario.delayNewMs) {
    signal.addEventListener("abort", () => log({ event: "requestCancelled" }), { once: true });
    await delay(scenario.delayNewMs, undefined, { signal });
  }
  const sessionId = scenario.sessionId ?? "session-" + randomUUID();
  state.sessions.push(sessionId);
  save();
  return { sessionId, ...sessionState() };
});
for (const name of [session.resume, session.load]) {
  request(name, async ({ params: { sessionId }, client }) => {
    if (scenario.resumeError) throw new RequestError(scenario.resumeError.code, scenario.resumeError.message);
    if (!state.sessions.includes(sessionId)) throw RequestError.resourceNotFound(sessionId);
    if (name === session.load) {
      for (const update of scenario.replay ?? []) await client.notify(methods.client.session.update, { sessionId, update });
    }
    return sessionState();
  });
}
request(session.close, () => ({}));
request(session.setMode, ({ params }) => {
  scenario.modes.currentModeId = params.modeId;
  return {};
});
request(session.setConfigOption, ({ params }) => {
  scenario.configOptions.find(option => option.id === params.configId).currentValue = params.value;
  return { configOptions: scenario.configOptions };
});
request(session.prompt, async ({ params: { sessionId }, client }) => {
  const turn = scenario.turns?.[state.turn++] ?? {};
  save();
  const signal = (cancel = new AbortController()).signal;
  for (const step of turn.steps ?? []) {
    if (step.stderr) writeFileSync(2, step.stderr + "\n");
    if (step.update) {
      await client.notify(methods.client.session.update, { sessionId: step.updateForSession ?? sessionId, update: step.update });
    } else if (step.permission) {
      const options = step.cancelPermissionMs === undefined ? {} : { cancellationSignal: AbortSignal.timeout(step.cancelPermissionMs) };
      const { outcome } = await client.request(methods.client.session.requestPermission, { sessionId, ...step.permission }, options);
      log({ event: "permission", outcome });
    } else if (step.delayMs !== undefined) {
      try { await delay(step.delayMs, undefined, { signal: turn.ignoreCancel ? undefined : signal }); }
      catch (error) { if (error.name !== "AbortError") throw error; }
    } else if (step.stdout !== undefined) {
      process.stdout.write(step.stdout + "\n");
    } else if (step.exit !== undefined) {
      process.exit(step.exit);
    } else if (step.readTextFile) {
      try { await client.request(methods.client.fs.readTextFile, { sessionId, path: step.readTextFile }); }
      catch (error) { log({ event: "readTextFileError", code: error.code }); }
    }
  }
  if (turn.afterResponse) {
    setImmediate(() => client.notify(methods.client.session.update, { sessionId, update: turn.afterResponse }));
  }
  return { stopReason: signal.aborted && !turn.ignoreCancel ? "cancelled" : turn.stopReason ?? "end_turn" };
});
app.onNotification(session.cancel, ({ params }) => {
  log({ method: session.cancel, params });
  cancel?.abort();
});
await app.connect(ndJsonStream(Writable.toWeb(process.stdout), Readable.toWeb(process.stdin))).closed;
