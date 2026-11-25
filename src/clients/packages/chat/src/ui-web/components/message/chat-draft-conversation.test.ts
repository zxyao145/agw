import assert from "node:assert/strict";
import test, { type TestContext } from "node:test";
import {
  installLayoutMetrics,
  setupDomEnvironment,
  replaceExecutionHub,
  startApiServer,
} from "@agw/test-harness";
import { configureApiRuntime, getApiRuntime } from "@agw/api";
import {
  configureExecutionRuntime,
  getExecutionRuntime,
  executionSessionManager,
} from "@agw/chat-runtime";
import { conversationComposerStorage } from "../../../lib/chat/conversation-composer-storage";
import type { ChatProps } from "./chat";

const { React, render, fireEvent, screen, waitFor, act, window } = await setupDomEnvironment();
installLayoutMetrics(window);
const { Chat } = await import("./chat.tsx");
const { QueryClient, QueryClientProvider } = await import("@agw/components/query");
const id = "0196f3a4-1c2d-7e8f-9a0b-0000000000a1";
const scope = { serverId: "browser", projectId: "project-a" };

async function fixture(t: TestContext) {
  const server = await startApiServer({
    "GET /api/agents/permission-capabilities": { supportedPermissionModes: ["fullAccess"] },
    "GET /api/agents/input-capabilities": { supportsSteer: false },
    "GET /api/agents/suggestions": {
      mode: "system",
      suggestions: [{ kind: "tool", text: "/mode_set", description: "Change mode" }],
    },
  });
  const previousApi = getApiRuntime(),
    previousExecution = getExecutionRuntime();
  configureApiRuntime({ baseUrl: server.baseUrl, token: null });
  configureExecutionRuntime({ baseUrl: "https://agw.test", token: null, attachmentStore: null });
  const hubs = replaceExecutionHub(t, { provider: "InProcess" });
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
  t.after(async () => {
    await executionSessionManager.discard({ ...scope, conversationId: id });
    executionSessionManager.conversationStatuses.removeScope(scope);
    conversationComposerStorage.removeConversations(scope);
    client.clear();
    configureExecutionRuntime(previousExecution);
    configureApiRuntime(previousApi);
  });
  function chat(props: Partial<ChatProps> = {}) {
    return React.createElement(
      QueryClientProvider,
      { client },
      React.createElement(Chat, {
        projectId: scope.projectId,
        target: { id: "agent-1", type: "agent" },
        conversationId: null,
        newConversationId: id,
        sessionSeed: {
          revision: 0,
          conversationId: null,
          messages: [],
          historyTurns: [],
          olderMessagesCursor: null,
          hasOlderMessages: false,
          agentMode: null,
        },
        ...props,
      }),
    );
  }
  async function send() {
    fireEvent.change(screen.getByRole("combobox", { name: "" }), { target: { value: "hello" } });
    const button = screen.getByRole("button", { name: "Send message" }) as HTMLButtonElement;
    await waitFor(() => assert.equal(button.disabled, false));
    fireEvent.click(button);
    await waitFor(() => assert.equal(hubs[0]?.commands("ExecCommand").length, 1));
    return hubs[0]!.commands("ExecCommand")[0]!;
  }
  return { chat, send, hubs };
}

test("a draft's first send uses newConversationId", async (t) => {
  const { chat, send, hubs } = await fixture(t);
  let submitted = 0;
  const view = render(
    chat({
      onSubmitted: () => {
        submitted++;
      },
    }),
  );
  await send();
  assert.equal(hubs[0]!.commands("SettingCommand")[0]!.conversationId, id);
  assert.equal(submitted, 1);
  view.unmount();
});

test("the input draft of a draft is stored under its draftId", async (t) => {
  const { chat } = await fixture(t);
  const view = render(chat({ persistInputDraft: true, draftId: "d1" }));
  fireEvent.change(screen.getByRole("combobox", { name: "" }), { target: { value: "typed" } });
  assert.deepEqual(conversationComposerStorage.get(scope, { draftId: "d1" }), { input: "typed" });
  assert.deepEqual(conversationComposerStorage.get(scope, { draftId: "d2" }), {});
  view.unmount();
});

test("a remounted draft attaches to its running session and reports acceptance", async (t) => {
  const { chat, send, hubs } = await fixture(t);
  const first = render(chat());
  const command = await send();
  first.unmount();
  const accepted: string[] = [];
  const second = render(
    chat({ keepLiveDuringRecovery: true, onConversationAccepted: (value) => accepted.push(value) }),
  );
  assert.equal(hubs.length, 1);
  act(() =>
    hubs[0]!.receive({
      messageId: "start",
      role: "system",
      contents: [],
      additionalProperties: { type: "agw-turn-start", conversationId: id, turnId: command.turnId },
    }),
  );
  await waitFor(() => assert.deepEqual(accepted, [id]));
  const turnId = command.turnId as string;
  second.rerender(
    chat({ conversationId: id, keepLiveDuringRecovery: true, isLoadingConversation: true }),
  );
  act(() => {
    hubs[0]!.receive({
      messageId: "new-mode",
      role: "system",
      contents: [],
      additionalProperties: { type: "mode-status", agentId: "agent-1", mode: "execute" },
    });
    hubs[0]!.receive({
      messageId: "answer",
      role: "assistant",
      contents: [{ type: "TextContent", content: "Completed while reading history" }],
      additionalProperties: {
        type: "result",
        turnId,
        conversationId: id,
        streamingScopeId: turnId,
      },
    });
    hubs[0]!.receive({
      messageId: "finished",
      role: "system",
      contents: [],
      additionalProperties: {
        type: "agw-turn-finished",
        status: "completed",
        turnId,
        conversationId: id,
        streamingScopeId: turnId,
      },
    });
  });
  second.rerender(
    chat({
      conversationId: id,
      keepLiveDuringRecovery: true,
      sessionSeed: {
        revision: 1,
        conversationId: id,
        historyTurns: [],
        hasOlderMessages: false,
        olderMessagesCursor: null,
        agentMode: "plan",
        messages: [
          {
            messageId: "older",
            role: "user",
            contents: [{ type: "TextContent", content: "Loaded earlier question" }],
            streamingScopeId: "earlier",
          },
        ],
      },
    }),
  );
  await waitFor(() => {
    assert.match(second.container.textContent!, /Loaded earlier question/);
    assert.match(second.container.textContent!, /Completed while reading history/);
    assert.match(second.container.textContent!, /hello/);
    assert.equal(screen.queryByRole("button", { name: "Turn plan mode off" }), null);
  });
  assert.equal(hubs.length, 1);
  second.unmount();
});

test("persistInputDraft without draftId throws", async (t) => {
  const { chat } = await fixture(t);
  assert.throws(
    () => render(chat({ persistInputDraft: true })),
    /persistInputDraft requires draftId\./,
  );
});

test("recovering a finished detached turn displays server history without replaying its capped tail", async (t) => {
  const { chat, send, hubs } = await fixture(t);
  const first = render(chat());
  const command = await send();
  first.unmount();
  const turnId = command.turnId as string;
  const incoming = (type: string, text = "") => ({
    messageId: type,
    role: type === "answer" ? "assistant" : "system",
    contents: text ? [{ type: "TextContent", content: text }] : [],
    additionalProperties: { type, turnId, conversationId: id, status: "completed" },
  });
  hubs[0]!.receive(incoming("agw-turn-start"));
  for (let n = 0; n < 220; n++) hubs[0]!.receive(incoming("answer", String(n)));
  hubs[0]!.receive(incoming("agw-turn-finished"));
  const replayed: string[] = [];
  const recovered = render(
    chat({
      keepLiveDuringRecovery: true,
      onExecutionMessage: (value) => replayed.push(value.messageId),
    }),
  );
  await act(async () => {});
  assert.deepEqual(replayed, []);
  recovered.rerender(
    chat({
      keepLiveDuringRecovery: true,
      conversationId: id,
      sessionSeed: {
        conversationId: id,
        revision: 1,
        historyTurns: [],
        olderMessagesCursor: null,
        hasOlderMessages: false,
        agentMode: "plan",
        messages: [
          {
            role: "user",
            messageId: "input",
            contents: [{ type: "TextContent", content: "First submitted question" }],
          },
          {
            role: "assistant",
            messageId: "answer",
            additionalProperties: { type: "result" },
            contents: [{ type: "TextContent", content: "Full persisted answer" }],
          },
        ],
      },
    }),
  );
  await waitFor(() => assert.match(recovered.container.textContent!, /First submitted question/));
  assert.match(recovered.container.textContent!, /Full persisted answer/);
  const switchMode = await screen.findByRole("button", { name: "Turn plan mode off" });
  fireEvent.click(switchMode);
  await waitFor(() =>
    assert.equal(screen.queryByRole("button", { name: "Turn plan mode off" }), null),
  );
  act(() =>
    hubs[0]!.receive({
      messageId: "rejected",
      role: "system",
      contents: [],
      additionalProperties: { type: "mode-change-failed", agentId: "agent-1", mode: "execute" },
    }),
  );
  assert.ok(await screen.findByRole("button", { name: "Turn plan mode off" }));
  assert.equal(hubs.length, 1);
  recovered.unmount();
});
