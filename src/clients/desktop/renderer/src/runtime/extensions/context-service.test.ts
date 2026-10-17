import assert from "node:assert/strict";
import test from "node:test";

import { apiGet, configureApiRuntime } from "@agw/api";
import { startApiServer } from "@agw/test-harness";
import type { HostContext } from "@desktop/shared/extension-api";
import { ContextService, connectionKeyOf, filterContextForPermissions } from "./context-service";

const app: HostContext["app"] = { version: "0.5.0", platform: "darwin", architecture: "arm64" };
const workspace = {
  sidebarVisible: true,
  activeSidebarViewId: "agw.conversations",
  activeMainViewId: "agw.chat",
};
const server = { id: "remote-1", name: "Office", kind: "remote" as const };

function selection(projectId: string) {
  return {
    project: { id: projectId, name: projectId, description: null, directories: [] },
    conversation: { id: `${projectId}-conversation` },
    target: {
      type: "agent" as const,
      id: "agent-1",
      name: "hello",
      displayName: "Hello",
      description: null,
      externalAgentKind: null,
    },
  };
}

test("changing any part of the connection identity clears the selection with a single notification", () => {
  for (const change of [
    { profileId: "remote-2", baseUrl: "https://office.example.test", token: "agw_a" },
    { profileId: "remote-1", baseUrl: "https://lab.example.test", token: "agw_a" },
    { profileId: "remote-1", baseUrl: "https://office.example.test", token: "agw_b" },
    { profileId: "remote-1", baseUrl: "https://office.example.test", token: null },
  ]) {
    const context = new ContextService(app, workspace);
    const identity = {
      profileId: "remote-1",
      baseUrl: "https://office.example.test/",
      token: "agw_a",
    };
    context.setConnection(identity, server);
    const key = connectionKeyOf(identity);
    context.setUser(key, { id: "user-1", displayName: "Ada", loginProvider: null, isAdmin: true });
    context.setSelection(key, selection("project-1"));

    const notifications: unknown[] = [];
    context.subscribe((snapshot) => notifications.push(snapshot));
    context.setConnection(change, { ...server, id: change.profileId });

    assert.equal(notifications.length, 1, JSON.stringify(change));
    const snapshot = context.snapshot();
    assert.equal(snapshot.user, null);
    assert.equal(snapshot.project, null);
    assert.equal(snapshot.conversation, null);
    assert.equal(snapshot.target, null);
    assert.equal(snapshot.server.id, change.profileId);
  }
});

test("writes carrying an old connection key are dropped", () => {
  const context = new ContextService(app, workspace);
  const oldIdentity = {
    profileId: "remote-1",
    baseUrl: "https://office.example.test",
    token: "agw_a",
  };
  context.setConnection(oldIdentity, server);
  const oldKey = connectionKeyOf(oldIdentity);
  context.setConnection({ ...oldIdentity, token: "agw_b" }, server);

  context.setSelection(oldKey, selection("stale"));
  context.setUser(oldKey, { id: "stale", displayName: null, loginProvider: null, isAdmin: false });
  assert.equal(context.snapshot().project, null);
  assert.equal(context.snapshot().user, null);

  context.setSelection(context.connectionKey!, selection("fresh"));
  assert.equal(context.snapshot().project?.id, "fresh");
});

test("requests stop without being sent when the API runtime no longer matches the identity", async () => {
  const api = await startApiServer({ "GET /api/projects": [] });
  const context = new ContextService(app, workspace);
  context.setConnection({ profileId: "local", baseUrl: api.baseUrl, token: "agw_a" }, server);

  configureApiRuntime({ baseUrl: api.baseUrl, token: "agw_a" });
  assert.deepEqual(await context.request((signal) => apiGet("/api/projects", { signal })), []);
  const sent = api.requests.length;

  configureApiRuntime({ baseUrl: api.baseUrl, token: "agw_b" });
  await assert.rejects(
    context.request((signal) => apiGet("/api/projects", { signal })),
    {
      code: "ERR_CONNECTION_CHANGED",
    },
  );
  configureApiRuntime({ baseUrl: "http://127.0.0.1:9", token: "agw_a" });
  await assert.rejects(
    context.request((signal) => apiGet("/api/projects", { signal })),
    {
      code: "ERR_CONNECTION_CHANGED",
    },
  );
  assert.equal(api.requests.length, sent);
});

test("pending requests end with ERR_CONNECTION_CHANGED when the identity changes", async () => {
  const context = new ContextService(app, workspace);
  const identity = { profileId: "local", baseUrl: "http://127.0.0.1:1", token: "agw_a" };
  configureApiRuntime({ baseUrl: identity.baseUrl, token: identity.token });
  context.setConnection(identity, server);
  let aborted = false;
  const pending = context.request(
    (signal) =>
      new Promise((_, reject) => {
        signal.addEventListener("abort", () => {
          aborted = true;
          reject(signal.reason);
        });
      }),
  );
  context.setConnection({ ...identity, token: "agw_b" }, server);
  await assert.rejects(pending, { code: "ERR_CONNECTION_CHANGED" });
  assert.equal(aborted, true);
});

test("context fields without the matching permission are removed", () => {
  const context = new ContextService(app, workspace);
  const identity = { profileId: "local", baseUrl: "http://127.0.0.1:1", token: null };
  context.setConnection(identity, server);
  context.setSelection(connectionKeyOf(identity), selection("project-1"));
  const full: HostContext = {
    ...context.snapshot(),
    extension: {
      id: "sample",
      version: "1.0.0",
      manifestVersion: 1,
      storageUri: "file:///data/sample",
    },
  };

  const none = filterContextForPermissions(full, []);
  assert.equal("user" in none, false);
  assert.equal("project" in none, false);
  assert.equal("conversation" in none, false);
  assert.equal("target" in none, false);
  assert.deepEqual(none.server, server);

  const project = filterContextForPermissions(full, ["context.project"]);
  assert.equal(project.project?.id, "project-1");
  assert.equal(project.conversation?.id, "project-1-conversation");
  assert.equal("target" in project, false);
  assert.equal(filterContextForPermissions(full, ["context.agent"]).target?.id, "agent-1");
  assert.equal(filterContextForPermissions(full, ["context.user"]).user, null);
});
