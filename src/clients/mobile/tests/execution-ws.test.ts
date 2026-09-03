import type { AiMessage } from "@agw/api";
import { HubConnectionBuilder, HubConnectionState } from "@microsoft/signalr";

import { buildMobileSettingCommand, MobileExecutionSession } from "@/features/chat/execution-ws";

type ReceiveMessage = (message: AiMessage) => void;

function createConnection(provider = "distributed") {
  const commands: Array<Record<string, unknown>> = [];
  let receiveMessage: ReceiveMessage | undefined;
  let reconnecting: (() => void) | undefined;
  let reconnected: (() => void) | undefined;
  let closed: ((error?: Error) => void) | undefined;
  const connection = {
    state: HubConnectionState.Disconnected,
    serverTimeoutInMilliseconds: 0,
    on: jest.fn((eventName: string, handler: ReceiveMessage) => {
      if (eventName === "ReceiveMessage") receiveMessage = handler;
    }),
    onreconnecting: jest.fn((handler: () => void) => {
      reconnecting = handler;
    }),
    onreconnected: jest.fn((handler: () => void) => {
      reconnected = handler;
    }),
    onclose: jest.fn((handler: (error?: Error) => void) => {
      closed = handler;
    }),
    start: jest.fn(async () => {
      connection.state = HubConnectionState.Connected;
    }),
    stop: jest.fn(async () => {
      connection.state = HubConnectionState.Disconnected;
    }),
    invoke: jest.fn(async (methodName: string, command?: Record<string, unknown>) => {
      if (methodName === "GetExecutionProvider") return provider;
      if (methodName === "DispatchCommand" && command) commands.push(command);
      return undefined;
    }),
  };

  return {
    connection,
    commands,
    emit: (message: AiMessage) => receiveMessage?.(message),
    reconnect: () => {
      connection.state = HubConnectionState.Reconnecting;
      reconnecting?.();
    },
    restore: () => {
      connection.state = HubConnectionState.Connected;
      reconnected?.();
    },
    close: (error?: Error) => {
      connection.state = HubConnectionState.Disconnected;
      closed?.(error);
    },
  };
}

function createSession(connection: ReturnType<typeof createConnection>) {
  const messages: AiMessage[] = [];
  const closeErrors: Error[] = [];
  const reconnectStates: unknown[] = [];
  jest
    .spyOn(HubConnectionBuilder.prototype, "build")
    .mockReturnValue(connection.connection as never);
  const session = new MobileExecutionSession({
    serverUrl: "https://agw.example.test",
    token: "token",
    onMessage: (message) => messages.push(message),
    onClose: (error) => closeErrors.push(error),
    onReconnecting: (state) => reconnectStates.push(state),
  });
  return { session, messages, closeErrors, reconnectStates };
}

function createInput(messageId: string) {
  return { messageId, author: "$agw", contents: [] };
}

function createMessage(
  messageId: string,
  additionalProperties: Record<string, unknown>,
): AiMessage {
  return {
    messageId,
    role: "system",
    author: "$agw",
    contents: [],
    additionalProperties,
  };
}

async function flushPromises(): Promise<void> {
  await new Promise<void>((resolve) => setImmediate(resolve));
}

afterEach(() => {
  jest.restoreAllMocks();
});

test("builds the Mobile execution setting through the shared protocol", () => {
  expect(
    buildMobileSettingCommand({
      projectId: "project-1",
      contextId: "context-1",
      permissionMode: "alwaysAsk",
    }),
  ).toEqual({
    type: "SettingCommand",
    projectId: "project-1",
    contextId: "context-1",
    permissionMode: "alwaysAsk",
  });
});

test("sets mode only when requested and reuses the connection across turns", async () => {
  const fake = createConnection();
  const { session, messages } = createSession(fake);
  await session.configure({
    projectId: "project-1",
    contextId: "context-1",
    permissionMode: "fullAccess",
  });
  await session.setMode("agent-1", "plan");

  const firstTurn = session.execute({
    conversationId: "conversation-1",
    agentId: "agent-1",
    agentType: 0,
    executionId: "execution-1",
    input: createInput("message-1"),
  });
  await flushPromises();
  fake.emit(
    createMessage("mode-1", {
      type: "mode-status",
      mode: "plan",
    }),
  );
  fake.emit(
    createMessage("finished-1", {
      type: "turn-finished",
      status: "completed",
      executionId: "execution-1",
    }),
  );
  await firstTurn;

  const secondTurn = session.execute({
    conversationId: "conversation-1",
    agentId: "agent-1",
    agentType: 0,
    executionId: "execution-2",
    input: createInput("message-2"),
  });
  await flushPromises();
  fake.emit(
    createMessage("finished-2", {
      type: "turn-finished",
      status: "completed",
      executionId: "execution-2",
    }),
  );
  await secondTurn;

  expect(fake.connection.start).toHaveBeenCalledTimes(1);
  expect(fake.commands.filter((command) => command.type === "SetModeCommand")).toEqual([
    { type: "SetModeCommand", agentId: "agent-1", mode: "plan" },
  ]);
  expect(fake.commands.filter((command) => command.type === "ExecCommand")).toHaveLength(2);
  expect(messages.map((message) => message.messageId)).toEqual([
    "mode-1",
    "finished-1",
    "finished-2",
  ]);
  expect(fake.connection.stop).not.toHaveBeenCalled();

  await session.dispose();
  expect(fake.connection.stop).toHaveBeenCalledTimes(1);
});

test("reconnect restores settings and the active turn without resending mode", async () => {
  const fake = createConnection();
  const { session, reconnectStates } = createSession(fake);
  await session.configure({
    projectId: "project-1",
    contextId: "context-1",
    permissionMode: "fullAccess",
  });
  await session.setPermissionMode("alwaysAsk");
  await session.setMode("agent-1", "plan");
  const turn = session.execute({
    conversationId: "conversation-1",
    agentId: "agent-1",
    agentType: 0,
    executionId: "execution-1",
    input: createInput("message-1"),
  });
  await flushPromises();

  fake.reconnect();
  fake.restore();
  await flushPromises();

  expect(fake.commands.filter((command) => command.type === "SettingCommand")).toEqual([
    {
      type: "SettingCommand",
      projectId: "project-1",
      contextId: "context-1",
      permissionMode: "fullAccess",
    },
    {
      type: "SettingCommand",
      projectId: "project-1",
      contextId: "context-1",
      permissionMode: "alwaysAsk",
    },
  ]);
  expect(fake.commands.filter((command) => command.type === "SubscribeExecutionCommand")).toEqual([
    { type: "SubscribeExecutionCommand", executionId: "execution-1" },
  ]);
  expect(fake.commands.filter((command) => command.type === "SetModeCommand")).toHaveLength(1);
  expect(reconnectStates.at(-1)).toBeNull();

  fake.emit(
    createMessage("finished-1", {
      type: "turn-finished",
      status: "completed",
      executionId: "execution-1",
    }),
  );
  await turn;
  await session.dispose();
});

test("interrupt targets the active execution", async () => {
  const fake = createConnection();
  const { session } = createSession(fake);
  await session.configure({
    projectId: "project-1",
    contextId: "context-1",
    permissionMode: "fullAccess",
  });
  const turn = session.execute({
    conversationId: "conversation-1",
    agentId: "agent-1",
    agentType: 0,
    executionId: "execution-1",
    input: createInput("message-1"),
  });
  await flushPromises();
  await session.interrupt("Stop requested by user.");

  expect(fake.commands.at(-1)).toEqual({
    type: "InterruptCommand",
    executionId: "execution-1",
    reason: "Stop requested by user.",
  });

  fake.emit(
    createMessage("finished-1", {
      type: "turn-finished",
      status: "interrupted",
      executionId: "execution-1",
    }),
  );
  await turn;
  await session.dispose();
});

test("reports a closed persistent connection so Workspace can replace the session", async () => {
  const fake = createConnection();
  const { session, closeErrors } = createSession(fake);
  await session.configure({
    projectId: "project-1",
    contextId: "context-1",
    permissionMode: "fullAccess",
  });

  fake.close(new Error("offline"));

  expect(closeErrors.map((error) => error.message)).toEqual(["offline"]);
  await session.dispose();
});
