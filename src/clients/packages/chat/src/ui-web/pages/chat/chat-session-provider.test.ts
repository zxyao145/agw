import assert from "node:assert/strict";
import test, { type TestContext } from "node:test";
import {
  rawApiResponse,
  replaceExecutionHub,
  setupDomEnvironment,
  type ApiRoutes,
} from "@agw/test-harness";
import type { ChatSessionContextValue, ChatSessionProviderProps } from "./chat-session-provider";
import type { ChatProjectContextValue } from "./chat-project-provider";
import {
  configureExecutionRuntime,
  getExecutionRuntime,
  executionSessionManager,
} from "@agw/chat-runtime";
import { conversationComposerStorage } from "../../../lib/chat/conversation-composer-storage";

const { React, act, render, waitFor } = await setupDomEnvironment();
const { providerFixture } = await import("./provider-test-fixture");
const { ChatSessionProvider, useChatSession } = await import("./chat-session-provider");
const { useChatProject } = await import("./chat-project-provider");
const id = "0196f3a4-1c2d-7e8f-9a0b-0000000000a1";
const scope = { projectId: "a", serverId: "browser" };
const key = { ...scope, conversationId: id };
const detailsPath = `/api/projects/a/conversations/${id}`;
const details = {
  projectId: "a",
  conversationId: id,
  contextId: "ctx",
  title: "My conversation",
  kind: "native",
  createTime: "2026-10-17T00:00:00Z",
  executionCount: 1,
  messageCount: 1,
  usage: {
    inputTokenCount: 0,
    outputTokenCount: 0,
    totalTokenCount: 0,
    cachedInputTokenCount: 0,
    reasoningTokenCount: 0,
    unsettledTurnIds: [],
  },
};
const messages = {
  items: [{ role: "user", messageId: "msg", contents: [] }],
  turns: [],
  hasMore: false,
};

async function mount(
  t: TestContext,
  overrides: Partial<ChatSessionProviderProps>,
  routes: ApiRoutes = {},
) {
  const { server, wrap } = await providerFixture(t, {
    [`GET ${detailsPath}`]: details,
    [`GET ${detailsPath}/messages`]: messages,
    ...routes,
  });
  let session!: ChatSessionContextValue;
  let project!: ChatProjectContextValue;
  function Probe() {
    session = useChatSession();
    project = useChatProject();
    return null;
  }
  const accepted: string[] = [],
    titles: string[] = [];
  let lost = 0;
  let props: ChatSessionProviderProps = {
    conversationId: null,
    draftId: id,
    newConversationId: id,
    draftSubmitted: false,
    visible: true,
    children: React.createElement(Probe),
    onConversationAccepted: (value) => accepted.push(value),
    onTitleChange: (title) => titles.push(title),
    onDraftLost: () => {
      lost++;
    },
    ...overrides,
  };
  const element = () => wrap(React.createElement(ChatSessionProvider, props));
  const view = render(element());
  await waitFor(() => assert.equal(project.selectedProjectId, "a"));
  t.after(async () => {
    view.unmount();
    await executionSessionManager.discard(key);
    executionSessionManager.conversationStatuses.removeScope(scope);
    conversationComposerStorage.removeConversations(scope);
  });
  return {
    accepted,
    titles,
    get lost() {
      return lost;
    },
    get session() {
      return session;
    },
    get project() {
      return project;
    },
    detailRequests: () => server.requests.filter((r) => r.path === detailsPath).length,
    change(next: Partial<ChatSessionProviderProps>) {
      props = { ...props, ...next };
      view.rerender(element());
    },
  };
}

test("mounting a conversation reads its history and reports the title", async (t) => {
  const run = await mount(t, { conversationId: id });
  await waitFor(() => {
    assert.equal(run.session.loadError, null);
    assert.deepEqual(run.titles, ["My conversation"]);
  });
  assert.deepEqual(run.session.chatSessionSeed.messages, messages.items);
  assert.equal(run.session.restoreExecution, true);
});

test("an accepted draft keeps its session when conversationId becomes the same id", async (t) => {
  const run = await mount(t, {});
  conversationComposerStorage.set(scope, { draftId: id }, { input: "next" });
  const revision = run.session.chatSessionSeed.revision;
  act(() => {
    run.session.handleConversationAccepted(id);
    run.session.handleConversationAccepted(id);
  });
  run.change({ conversationId: id });
  assert.deepEqual(run.accepted, [id]);
  assert.equal(run.detailRequests(), 0);
  assert.equal(run.session.chatSessionSeed.revision, revision);
  assert.deepEqual(conversationComposerStorage.get(scope, { conversationId: id }), {
    input: "next",
  });
  assert.deepEqual(conversationComposerStorage.get(scope, { draftId: id }), {});
});

test("a failed read shows loadError and retry reads again", async (t) => {
  let failed = true;
  const run = await mount(
    t,
    { conversationId: id },
    {
      [`GET ${detailsPath}`]: () =>
        failed ? rawApiResponse({ status: 503, body: "Offline" }) : details,
    },
  );
  await waitFor(() => assert.ok(run.session.loadError));
  failed = false;
  act(() => run.session.retry());
  await waitFor(() => assert.deepEqual(run.titles, [details.title]));
  assert.equal(run.session.loadError, null);
  assert.equal(run.detailRequests(), 2);
});

test("a submitted draft with a known status is accepted at once", async (t) => {
  executionSessionManager.conversationStatuses.turnStarted(scope, id, "turn");
  const run = await mount(t, { draftSubmitted: true });
  assert.deepEqual(run.accepted, [id]);
  run.change({ conversationId: id, draftSubmitted: false });
  await waitFor(() => assert.deepEqual(run.titles, [details.title]));
});

test("a submitted draft with a running session is accepted when its status appears", async (t) => {
  const previous = getExecutionRuntime();
  configureExecutionRuntime({ baseUrl: "https://agw.test", token: null, attachmentStore: null });
  replaceExecutionHub(t, { provider: "InProcess" });
  t.after(() => configureExecutionRuntime(previous));
  const handle = executionSessionManager.attach(key, { onMessage() {} });
  const run = await mount(t, { draftSubmitted: true });
  assert.deepEqual(run.accepted, []);
  act(() => executionSessionManager.conversationStatuses.turnStarted(scope, id, "turn"));
  assert.deepEqual(run.accepted, [id]);
  run.change({ conversationId: id, draftSubmitted: false });
  await waitFor(() => assert.deepEqual(run.session.chatSessionSeed.messages, messages.items));
  assert.equal(run.detailRequests(), 1);
  handle.detach();
});

test("a submitted draft found on the Server after a restart is accepted", async (t) => {
  const run = await mount(t, { draftSubmitted: true });
  await waitFor(() => assert.deepEqual(run.accepted, [id]));
  run.change({ conversationId: id, draftSubmitted: false });
  await waitFor(() => assert.deepEqual(run.session.chatSessionSeed.messages, messages.items));
  assert.equal(run.detailRequests(), 1);
});

test("a recovered draft stays accepted when its history needs a retry", async (t) => {
  let offline = true;
  const run = await mount(
    t,
    { draftSubmitted: true },
    {
      [`GET ${detailsPath}/messages`]: () => (offline ? rawApiResponse({ status: 503 }) : messages),
    },
  );
  await waitFor(() => assert.deepEqual(run.accepted, [id]));
  run.change({ conversationId: id, draftSubmitted: false });
  await waitFor(() => assert.ok(run.session.loadError));
  offline = false;
  act(() => run.session.retry());
  await waitFor(() => assert.equal(run.session.restoreExecution, true));
  assert.equal(run.detailRequests(), 1);
  assert.deepEqual(run.accepted, [id]);
});

test("a submitted draft missing on the Server is reported lost", async (t) => {
  const run = await mount(
    t,
    { draftSubmitted: true },
    { [`GET ${detailsPath}`]: rawApiResponse({ status: 404 }) },
  );
  await waitFor(() => assert.equal(run.lost, 1));
  assert.deepEqual(run.accepted, []);
  assert.equal(run.session.loadError, null);
  run.change({ draftSubmitted: false });
  const previous = getExecutionRuntime();
  configureExecutionRuntime({ baseUrl: "https://agw.test", token: null, attachmentStore: null });
  const handle = executionSessionManager.attach(key, { onMessage() {} });
  try {
    run.change({ draftSubmitted: true });
    act(() => run.session.handleConversationAccepted(id));
    run.change({ conversationId: id, draftSubmitted: false });
    assert.deepEqual(run.accepted, [id]);
    assert.equal(run.session.keepLive, false);
    assert.equal(run.session.isLoadingConversation, false);
    assert.equal(run.session.chatSessionSeed.revision, 0);
  } finally {
    handle.detach();
    configureExecutionRuntime(previous);
  }
});

test("only a visible session reports its reconnect state", async (t) => {
  const run = await mount(t, { visible: false });
  act(() =>
    run.session.handleReconnectStateChange(
      { status: "reconnecting", retryAttempt: 6, retryDelayMs: 0 },
      async () => {},
    ),
  );
  assert.equal(run.project.isReconnecting, false);
  run.change({ visible: true });
  assert.equal(run.project.isReconnecting, true);
  run.change({ visible: false });
  assert.equal(run.project.isReconnecting, false);
});
