import assert from "node:assert/strict";
import test from "node:test";
import { HubConnectionBuilder, HubConnectionState } from "@microsoft/signalr";
import type { AiMessage } from "@agw/api";
import { ExecutionSession } from "./execution-session";

function update(text: string): AiMessage {
  return {
    messageId: "message",
    role: "assistant",
    author: "test",
    contents: [{ type: "TextContent", content: text, additionalProperties: { blockId: "text" } }],
    additionalProperties: {
      conversationGeneration: 3,
    },
  };
}

test("existing message channel delivers every delta without revision negotiation or recovery", async (t) => {
  const handlers = new Map<string, (message: AiMessage) => void>();
  const calls: { method: string; args: unknown[] }[] = [];
  const received: AiMessage[] = [];
  const errors: Error[] = [];
  const connection = {
    state: HubConnectionState.Disconnected,
    on(name: string, handler: (message: AiMessage) => void) {
      handlers.set(name, handler);
    },
    onclose() {},
    onreconnecting() {},
    onreconnected() {},
    async start() {
      connection.state = HubConnectionState.Connected;
    },
    async stop() {
      connection.state = HubConnectionState.Disconnected;
    },
    async invoke(method: string, ...args: unknown[]) {
      calls.push({ method, args });
      if (method === "GetExecutionProvider") return "InProcess";
      if (method === "FindInProcessExecution") return null;
    },
  };
  t.mock.method(HubConnectionBuilder.prototype, "build", () => connection as never);
  const session = new ExecutionSession(
    { onMessage: (message) => received.push(message), onError: (error) => errors.push(error) },
    { baseUrl: "https://agw.test", token: null, attachmentStore: null },
  );
  try {
    await session.configure({ projectId: "project", contextId: "context" });
    const receive = handlers.get("ReceiveMessage")!;
    receive(update("a"));
    receive(update("a"));
    receive(update(" "));
    receive(update("b"));
    assert.deepEqual(
      received.map((message) => message.contents[0].content),
      ["a", "a", " ", "b"],
    );
    assert.deepEqual(errors, []);
    assert.equal(
      calls.some(
        (call) =>
          call.method === "NegotiateMessageProtocol" || call.method === "GetMessageSnapshot",
      ),
      false,
    );
  } finally {
    await session.dispose();
  }
});
