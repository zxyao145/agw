import assert from "node:assert/strict";
import test from "node:test";

import { clearAntiforgeryToken } from "@agw/api";
import {
  archiveProviderSession,
  getProviderSessions,
  groupProviderSessions,
  type ProviderSessionRecord,
} from "./provider-sessions";

test.beforeEach(() => clearAntiforgeryToken());
test.afterEach(() => clearAntiforgeryToken());

const AGENT_A = "019a1234-5678-7000-8000-00000000000a";
const AGENT_B = "019a1234-5678-7000-8000-00000000000b";

function record(
  id: string,
  overrides: Partial<ProviderSessionRecord> & Pick<ProviderSessionRecord, "createTime">,
): ProviderSessionRecord {
  return {
    id,
    agentId: AGENT_A,
    externalAgentName: "codex",
    providerSessionId: `session-${id}`,
    isActive: false,
    updateTime: null,
    ...overrides,
  };
}

test("groupProviderSessions separates agents that share an external agent name", () => {
  const groups = groupProviderSessions([
    record("1", { createTime: "2026-10-09T02:00:00Z", isActive: true }),
    record("2", { agentId: AGENT_B, createTime: "2026-10-09T03:00:00Z", isActive: true }),
  ]);

  assert.deepEqual(
    groups.map((group) => [group.agentId, group.externalAgentName, group.active?.id]),
    [
      [AGENT_B, "codex", "2"],
      [AGENT_A, "codex", "1"],
    ],
  );
});

test("groupProviderSessions keeps the same session id in different groups apart", () => {
  const groups = groupProviderSessions([
    record("1", {
      createTime: "2026-10-09T02:00:00Z",
      isActive: true,
      providerSessionId: "shared",
    }),
    record("2", {
      createTime: "2026-10-09T02:00:00Z",
      externalAgentName: "pi",
      isActive: false,
      providerSessionId: "shared",
    }),
  ]);

  assert.equal(groups.length, 2);
  const codex = groups.find((group) => group.externalAgentName === "codex");
  const pi = groups.find((group) => group.externalAgentName === "pi");
  assert.equal(codex?.active?.id, "1");
  assert.deepEqual(codex?.history, []);
  assert.equal(pi?.active, null);
  assert.deepEqual(
    pi?.history.map((item) => item.id),
    ["2"],
  );
});

test("groupProviderSessions orders history newest first by creation time then id", () => {
  const groups = groupProviderSessions([
    record("a", { createTime: "2026-10-09T01:00:00Z" }),
    record("c", { createTime: "2026-10-09T02:00:00Z" }),
    record("b", { createTime: "2026-10-09T02:00:00Z" }),
    record("d", { createTime: "2026-10-09T03:00:00Z", isActive: true }),
  ]);

  const group = groups[0];
  assert.equal(group.active?.id, "d");
  assert.deepEqual(
    group.history.map((item) => item.id),
    ["c", "b", "a"],
  );
});

test("groupProviderSessions keeps a fully archived group with its history", () => {
  const groups = groupProviderSessions([
    record("1", { createTime: "2026-10-09T01:00:00Z", updateTime: "2026-10-09T02:00:00Z" }),
    record("2", { createTime: "2026-10-09T02:00:00Z", updateTime: "2026-10-09T03:00:00Z" }),
  ]);

  assert.equal(groups.length, 1);
  assert.equal(groups[0].active, null);
  assert.deepEqual(
    groups[0].history.map((item) => item.id),
    ["2", "1"],
  );
});

test("getProviderSessions reads the conversation's records through query parameters", async (t) => {
  const originalFetch = globalThis.fetch;
  const requests: string[] = [];
  globalThis.fetch = (async (input: RequestInfo | URL) => {
    requests.push(String(input));
    return Response.json({
      code: 0,
      title: "OK",
      data: [record("1", { createTime: "2026-10-09T02:00:00Z", isActive: true })],
    });
  }) as typeof fetch;
  t.after(() => {
    globalThis.fetch = originalFetch;
  });

  const records = await getProviderSessions({
    projectId: "project-1",
    conversationId: "conversation-1",
  });

  assert.deepEqual(requests, [
    "/api/projects/conversations/provider-sessions?projectId=project-1&conversationId=conversation-1",
  ]);
  assert.equal(records[0].providerSessionId, "session-1");
});

test("archiveProviderSession posts the record id in the body", async (t) => {
  const originalFetch = globalThis.fetch;
  const requests: Array<{ url: string; body?: unknown }> = [];
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    if (String(input) === "/api/auth/antiforgery") {
      return Response.json({ code: 200, title: "OK", data: { requestToken: "csrf" } });
    }
    requests.push({ url: String(input), body: JSON.parse(String(init?.body)) });
    return Response.json({ code: 0, title: "OK" });
  }) as typeof fetch;
  t.after(() => {
    globalThis.fetch = originalFetch;
  });

  await archiveProviderSession({
    projectId: "project-1",
    conversationId: "conversation-1",
    bindingId: "binding-1",
  });

  assert.deepEqual(requests, [
    {
      url: "/api/projects/conversations/provider-sessions/archive",
      body: { projectId: "project-1", conversationId: "conversation-1", bindingId: "binding-1" },
    },
  ]);
});
