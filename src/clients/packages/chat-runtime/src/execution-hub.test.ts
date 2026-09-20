import assert from "node:assert/strict";
import test from "node:test";
import { HubConnectionBuilder, HubConnectionState, type IRetryPolicy } from "@microsoft/signalr";
import type { AiMessage } from "@agw/api";

test("missing execution provider capability fails configuration without dispatching settings", async (t) => {
  const { ExecutionSession } = await import("./execution-session.ts");
  const calls: string[] = [];
  let failures = 0;
  const connection = {
    state: HubConnectionState.Connected,
    on() {},
    onclose() {},
    onreconnecting() {},
    onreconnected() {},
    async start() {},
    async stop() {},
    async invoke(method: string) {
      calls.push(method);
      throw new Error("Unknown hub method GetExecutionProvider");
    },
  };
  t.mock.method(HubConnectionBuilder.prototype, "build", () => connection as never);
  const session = new ExecutionSession(
    {
      onMessage() {},
      onReconnectFailed() {
        failures++;
      },
    },
    { baseUrl: "https://agw.test", token: null, attachmentStore: null },
  );
  try {
    await assert.rejects(
      session.configure({ projectId: "project", contextId: "context" }),
      /Unknown hub method/,
    );
    assert.deepEqual(calls, ["GetExecutionProvider"]);
    assert.equal(failures, 1);
  } finally {
    await session.dispose();
  }
});

test("concurrent Agentflow initialization and checkpoint queries share the connecting hub", async (t) => {
  const { ExecutionSession } = await import("./execution-session.ts");
  const connected = Promise.withResolvers<void>();
  let starts = 0;
  const failures: unknown[] = [];
  const connection = {
    state: HubConnectionState.Disconnected,
    on() {},
    onclose() {},
    onreconnecting() {},
    onreconnected() {},
    async start() {
      starts++;
      connection.state = HubConnectionState.Connecting;
      await connected.promise;
      connection.state = HubConnectionState.Connected;
    },
    async stop() {
      connection.state = HubConnectionState.Disconnected;
    },
    async invoke(method: string) {
      if (method === "GetExecutionProvider") return "InProcess";
      if (method === "FindInProcessExecution") return null;
      if (method === "GetAgentflowCheckpoints") return [];
    },
  };
  t.mock.method(HubConnectionBuilder.prototype, "build", () => connection as never);
  const session = new ExecutionSession(
    { onMessage() {}, onReconnectFailed: (state) => failures.push(state) },
    { baseUrl: "https://agw.test", token: null, attachmentStore: null },
  );
  try {
    const setting = { projectId: "project", contextId: "context" };
    const results = Promise.allSettled([
      session.configure(setting),
      session.configure(setting),
      session.listAgentflowCheckpoints("flow"),
    ]);
    connected.resolve();
    assert.deepEqual(
      (await results).map((result) => result.status),
      ["fulfilled", "fulfilled", "fulfilled"],
    );
    assert.equal(starts, 1);
    assert.deepEqual(failures, []);
  } finally {
    await session.dispose();
  }
});

test("Retry recovers a WebSocket-only Agentflow turn when SignalR has no connectionId", async (t) => {
  const { ExecutionSession } = await import("./execution-session.ts");
  t.mock.timers.enable({ apis: ["setTimeout"] });
  let close!: (error: Error) => void;
  let active = false;
  let restored = 0;
  let stops = 0;
  let upperCloses = 0;
  let reconnectFailures = 0;
  const recoveryCalls: unknown[][] = [];
  const connection = {
    connectionId: null,
    state: HubConnectionState.Disconnected,
    on() {},
    onreconnecting() {},
    onreconnected() {},
    onclose(handler: typeof close) {
      close = handler;
    },
    async start() {
      connection.state = HubConnectionState.Connected;
    },
    async stop() {
      stops++;
      connection.state = HubConnectionState.Disconnected;
    },
    async invoke(method: string, ...args: unknown[]) {
      if (method === "GetExecutionProvider") return "InProcess";
      if (method === "FindInProcessExecution") return active ? "original-connection" : null;
      if (method === "RecoverInProcessExecution") {
        recoveryCalls.push(args);
        return active;
      }
    },
  };
  t.mock.method(HubConnectionBuilder.prototype, "build", () => connection as never);
  const session = new ExecutionSession(
    {
      onMessage() {},
      onClose() {
        upperCloses++;
      },
      onReconnectFailed() {
        reconnectFailures++;
      },
      onReconnected() {
        restored++;
      },
    },
    { baseUrl: "https://agw.test", token: null, attachmentStore: null },
  );
  try {
    await session.configure({ projectId: "project", contextId: "context" });
    await session.execute({
      conversationId: "conversation",
      agentId: "flow",
      agentType: 1,
      input: { messageId: "input", author: "$agw", contents: [] },
    });
    active = true;
    connection.state = HubConnectionState.Disconnected;
    close(new Error("Connection lost"));
    assert.equal(session.hasActiveExecution(), true);
    assert.equal(upperCloses, 0, "an active transport close must not reach the UI onClose handler");
    assert.equal(reconnectFailures, 1);
    void session.retryConnection();
    for (let i = 0; i < 40; i++) await Promise.resolve();
    assert.equal(restored, 1);
    assert.equal(stops, 0);
    assert.deepEqual(recoveryCalls, [["original-connection", false]]);
    assert.equal(session.hasActiveExecution(), true);
    await session.interrupt();
    assert.deepEqual(recoveryCalls.at(-1), ["original-connection", true]);

    // A later turn also has no negotiated ID; it may finish while disconnected.
    active = false;
    await session.interrupt();
    const restoredBeforeNextTurn = restored;
    await session.execute({
      conversationId: "conversation",
      agentId: "flow",
      agentType: 1,
      input: { messageId: "next-input", author: "$agw", contents: [] },
    });
    connection.state = HubConnectionState.Disconnected;
    close(new Error("Connection lost after completion"));
    await session.retryConnection();
    assert.equal(restored, restoredBeforeNextTurn + 1);
    assert.equal(session.hasActiveExecution(), false);
    assert.equal(stops, 0);
  } finally {
    await session.dispose();
  }
});

test("human response dispatch binds the nested response to the requested or active execution", async () => {
  const { ExecutionSession } = await import("./execution-session.ts");
  const commands: unknown[] = [];
  const session = Object.assign(Object.create(ExecutionSession.prototype), {
    activeExecutionId: "active-execution",
    dispatch: async (command: unknown) => {
      commands.push(command);
    },
  }) as InstanceType<typeof ExecutionSession>;
  const response = { kind: "user-input" as const, interactionId: "input-1", cancelled: true };

  await session.submitHumanResponse({ response, executionId: undefined });
  await session.submitHumanResponse({ response, executionId: "request-execution" });

  assert.deepEqual(commands, [
    { type: "HumanResponseCommand", executionId: "active-execution", response },
    { type: "HumanResponseCommand", executionId: "request-execution", response },
  ]);
});

test("buildSettingCommand keeps target data out of settings", async () => {
  const { buildSettingCommand } = await import("./execution-hub" + ".ts");

  assert.deepEqual(
    buildSettingCommand({
      projectId: "project-1",
      contextId: "context-1",
      environmentVariables: { TOKEN: "value" },
      permissionMode: "fullAccess",
    }),
    {
      type: "SettingCommand",
      projectId: "project-1",
      contextId: "context-1",
      environmentVariables: { TOKEN: "value" },
      permissionMode: "fullAccess",
    },
  );
});

test("buildSetModeCommand uses the direct execution command protocol", async () => {
  const { buildSetModeCommand } = await import("./execution-hub" + ".ts");

  assert.deepEqual(buildSetModeCommand("agent-1", "execute"), {
    type: "SetModeCommand",
    agentId: "agent-1",
    mode: "execute",
  });
});

test("buildSetPermissionModeCommand uses the dynamic permission protocol", async () => {
  const { buildSetPermissionModeCommand } = await import("./execution-hub" + ".ts");

  assert.deepEqual(buildSetPermissionModeCommand("allowSameArguments"), {
    type: "SetPermissionModeCommand",
    permissionMode: "allowSameArguments",
  });
});

test("getAgentMode reads direct and tool mode status messages", async () => {
  const { getAgentMode, isModeControlMessage } = await import("./execution-hub" + ".ts");
  const directStatus = {
    messageId: "mode-1",
    role: "system" as const,
    author: "$agw",
    contents: [],
    additionalProperties: { type: "mode-status", mode: "plan" },
  };

  assert.equal(getAgentMode(directStatus), "plan");
  assert.equal(isModeControlMessage(directStatus), true);
  assert.equal(
    getAgentMode({
      ...directStatus,
      additionalProperties: { type: "tool-mode-status", mode: "execute" },
    }),
    "execute",
  );
});

test("buildExecCommand includes target and streaming mode", async () => {
  const { buildExecCommand } = await import("./execution-hub" + ".ts");
  const input = { messageId: "message-1", author: "$agw", contents: [] };

  assert.deepEqual(
    buildExecCommand({
      conversationId: "conversation-1",
      agentId: "agent-1",
      agentType: 0,
      stream: false,
      input,
    }),
    {
      type: "ExecCommand",
      conversationId: "conversation-1",
      agentId: "agent-1",
      agentType: 0,
      stream: false,
      input,
    },
  );
});

test("buildExecCommand includes a durable execution identity when supplied", async () => {
  const { buildExecCommand } = await import("./execution-hub" + ".ts");
  const input = { messageId: "message-1", author: "$agw", contents: [] };

  assert.deepEqual(
    buildExecCommand({
      conversationId: "conversation-1",
      executionId: "execution-1",
      agentId: "agent-1",
      agentType: 0,
      input,
    }),
    {
      type: "ExecCommand",
      conversationId: "conversation-1",
      executionId: "execution-1",
      agentId: "agent-1",
      agentType: 0,
      stream: true,
      input,
    },
  );
});

test("buildSubscribeExecutionCommand resumes a Redis stream cursor", async () => {
  const { buildSubscribeExecutionCommand } = await import("./execution-hub" + ".ts");

  assert.deepEqual(buildSubscribeExecutionCommand("execution-1", "3-9"), {
    type: "SubscribeExecutionCommand",
    executionId: "execution-1",
    cursor: "3-9",
  });
});

test("checkpoint commands preserve the exact occurrence identity", async () => {
  const { buildResumeCheckpointCommand, getAgentflowCheckpointMessage } = await import(
    "./execution-hub" + ".ts"
  );

  assert.deepEqual(
    buildResumeCheckpointCommand({
      checkpointOccurrenceId: "occurrence-1",
      resumeExecutionId: "execution-2",
      agentflowId: "agentflow-1",
    }),
    {
      type: "ResumeCheckpointCommand",
      checkpointOccurrenceId: "occurrence-1",
      resumeExecutionId: "execution-2",
      agentflowId: "agentflow-1",
    },
  );
  assert.deepEqual(
    getAgentflowCheckpointMessage({
      messageId: "checkpoint-message-1",
      author: "Agw",
      role: "assistant",
      contents: [{ type: "TextContent", content: "Review saved" }],
      additionalProperties: {
        type: "agentflow-checkpoint",
        checkpointOccurrenceId: "occurrence-1",
        checkpointNodeId: "checkpoint-node",
        checkpointName: "Review saved",
      },
    }),
    {
      occurrenceId: "occurrence-1",
      nodeId: "checkpoint-node",
      name: "Review saved",
    },
  );
});

test("execution session keeps tool rendering scope across handler replacement and terminal turns", async () => {
  const { ExecutionHubClient } = await import("./execution-hub" + ".ts");
  const { buildConversationRenderModel } = await import("@agw/chat-core");
  const originalBuild = HubConnectionBuilder.prototype.build;
  let receiveMessage: ((message: AiMessage) => void) | undefined;
  const connection = {
    state: HubConnectionState.Disconnected,
    on: (eventName: string, handler: (message: AiMessage) => void) => {
      if (eventName === "ReceiveMessage") receiveMessage = handler;
    },
    onreconnecting: () => undefined,
    onclose: () => undefined,
    onreconnected: () => undefined,
    start: async () => {
      connection.state = HubConnectionState.Connected;
    },
    stop: async () => {
      connection.state = HubConnectionState.Disconnected;
    },
    invoke: async (methodName: string) =>
      methodName === "GetExecutionProvider"
        ? "InProcess"
        : methodName === "FindInProcessExecution"
          ? null
          : undefined,
  };
  HubConnectionBuilder.prototype.build = () => connection as never;

  const transportMessages: AiMessage[] = [];
  const attachHandler = () => ({
    onMessage: (message: AiMessage) => transportMessages.push(message),
  });
  const emit = (message: AiMessage) => {
    assert.ok(receiveMessage);
    receiveMessage(message);
  };
  const toolCall = (messageId: string, callId: string, toolName: string) => ({
    messageId,
    role: "assistant",
    author: "agent",
    contents: [
      {
        type: "FunctionCallContent",
        content: "{}",
        additionalProperties: { callId, toolName },
      },
    ],
  });
  const toolResult = (messageId: string, callId: string) => ({
    messageId,
    role: "tool",
    author: "agent",
    contents: [
      {
        type: "FunctionResultContent",
        content: "done",
        additionalProperties: { callId },
      },
    ],
  });
  const turnState = (
    messageId: string,
    type: "turn-start" | "turn-finished",
    additionalProperties: Record<string, unknown> = {},
  ) => ({
    messageId,
    role: "system",
    author: "$agw",
    contents: [],
    additionalProperties: {
      type,
      ...(type === "turn-finished" ? { status: "completed" } : {}),
      ...additionalProperties,
    },
  });

  const client = new ExecutionHubClient(attachHandler(), {
    baseUrl: "https://agw.example.test",
    token: null,
    attachmentStore: null,
  });

  try {
    await client.configure({ projectId: "project-1", contextId: "context-1" });
    await client.execute({
      conversationId: "conversation-1",
      agentId: "agent-1",
      agentType: 0,
      input: { messageId: "user-1", author: "$agw", contents: [] },
    });
    emit(turnState("start-1", "turn-start"));
    emit(toolCall("call-read-1", "Read_1", "Read"));
    emit(toolCall("call-bash-1", "Bash_1", "Bash"));

    client.setHandlers(attachHandler());
    emit(toolResult("result-bash-1", "Bash_1"));
    emit(toolResult("result-read-1", "Read_1"));
    emit(turnState("finished-1", "turn-finished"));

    assert.deepEqual(
      transportMessages.slice(0, 6).map((message) => message.streamingScopeId),
      Array(6).fill("user-1"),
    );
    assert.deepEqual(
      buildConversationRenderModel(transportMessages).map((item) =>
        item.type === "tool-accordion" ? item.toolName : item.type,
      ),
      ["Read", "Bash"],
    );

    emit({
      messageId: "late-message",
      role: "assistant",
      author: "agent",
      contents: [{ type: "TextContent", content: "late" }],
    });
    assert.equal(transportMessages.at(-1)?.streamingScopeId, "late-message");

    await client.execute({
      conversationId: "conversation-1",
      agentId: "agent-1",
      agentType: 0,
      input: { messageId: "user-2", author: "$agw", contents: [] },
    });
    emit(
      turnState("start-2", "turn-start", {
        streamingScopeId: "server-user-2",
      }),
    );
    emit(toolCall("call-read-2", "Read_1", "Read"));
    emit(toolResult("result-read-2", "Read_1"));
    emit(turnState("finished-2", "turn-finished"));

    assert.deepEqual(
      transportMessages.slice(-4).map((message) => message.streamingScopeId),
      Array(4).fill("server-user-2"),
    );
    assert.deepEqual(
      buildConversationRenderModel(transportMessages)
        .filter((item) => item.type === "tool-accordion")
        .map((item) => item.toolName),
      ["Read", "Bash", "Read"],
    );
  } finally {
    await client.dispose();
    HubConnectionBuilder.prototype.build = originalBuild;
  }
});

test("manual reconnect consumes the current attempt and waits on the next after failure", async (t) => {
  const random = t.mock.method(Math, "random", () => 0.5);
  const { ExecutionHubClient } = await import("./execution-hub" + ".ts");
  const originalBuild = HubConnectionBuilder.prototype.build;
  let reconnectPolicy: IRetryPolicy | undefined;
  let reconnectingCallback: (() => void) | undefined;
  let closeCallback: (() => void) | undefined;
  let startCount = 0;
  const connection: {
    state: HubConnectionState;
    on: () => void;
    onreconnecting: (callback: () => void) => void;
    onclose: (callback: () => void) => void;
    onreconnected: () => void;
    start: () => Promise<void>;
    stop: () => Promise<void>;
    invoke: () => Promise<undefined>;
  } = {
    state: HubConnectionState.Disconnected,
    on: () => undefined,
    onreconnecting: (callback) => {
      reconnectingCallback = callback;
    },
    onclose: (callback) => {
      closeCallback = callback;
    },
    onreconnected: () => undefined,
    start: async () => {
      startCount += 1;
      if ([1, 3, 4].includes(startCount)) {
        connection.state = HubConnectionState.Disconnected;
        throw new Error("still offline");
      }
      connection.state = HubConnectionState.Connected;
    },
    stop: async () => {
      connection.state = HubConnectionState.Disconnected;
      closeCallback?.();
    },
    invoke: async () => undefined,
  };
  HubConnectionBuilder.prototype.build = function () {
    reconnectPolicy = this.reconnectPolicy;
    return connection as never;
  };

  const reconnectStates: Array<{
    status: "reconnecting" | "failed";
    retryAttempt: number;
    retryDelayMs: number;
  }> = [];
  let reconnectedCount = 0;
  const client = new ExecutionHubClient(
    {
      onMessage: () => undefined,
      onReconnecting: (state) => reconnectStates.push(state),
      onReconnectFailed: (state) => reconnectStates.push(state),
      onReconnected: () => {
        reconnectedCount += 1;
      },
    },
    { baseUrl: "https://agw.example.test", token: null, attachmentStore: null },
  );

  try {
    assert.ok(reconnectPolicy);
    const scheduledDelayMs = reconnectPolicy.nextRetryDelayInMilliseconds({
      previousRetryCount: 2,
      elapsedMilliseconds: 7_000,
      retryReason: new Error("offline"),
    });
    assert.equal(scheduledDelayMs, 3_300);
    assert.equal(random.mock.callCount(), 1, "schedule samples jitter once");
    connection.state = HubConnectionState.Reconnecting;
    reconnectingCallback?.();
    assert.deepEqual(reconnectStates.at(-1), {
      status: "reconnecting",
      retryAttempt: 3,
      retryDelayMs: 3_300,
    });

    assert.equal(
      reconnectStates.at(-1)?.retryDelayMs,
      scheduledDelayMs,
      "UI uses the actual scheduled delay",
    );
    const reconnectCycle = client.retryConnection();
    for (let index = 0; index < 20; index += 1) {
      if (reconnectStates.at(-1)?.retryAttempt === 4) break;
      await new Promise<void>((resolve) => setImmediate(resolve));
    }

    assert.equal(startCount, 1);
    assert.deepEqual(reconnectStates.slice(-2), [
      { status: "reconnecting", retryAttempt: 3, retryDelayMs: 0 },
      { status: "reconnecting", retryAttempt: 4, retryDelayMs: 5_500 },
    ]);

    const continuedCycle = client.retryConnection();
    await Promise.all([reconnectCycle, continuedCycle]);

    assert.equal(startCount, 2);
    assert.deepEqual(reconnectStates.at(-1), {
      status: "reconnecting",
      retryAttempt: 4,
      retryDelayMs: 0,
    });
    assert.equal(reconnectedCount, 1);

    reconnectPolicy.nextRetryDelayInMilliseconds({
      previousRetryCount: 9,
      elapsedMilliseconds: 44_000,
      retryReason: new Error("offline again"),
    });
    connection.state = HubConnectionState.Reconnecting;
    reconnectingCallback?.();
    const lastAttempt = client.retryConnection();
    await lastAttempt;

    assert.equal(startCount, 3);
    assert.deepEqual(reconnectStates.at(-1), {
      status: "failed",
      retryAttempt: 10,
      retryDelayMs: 0,
    });

    const restartedCycle = client.retryConnection();
    for (let index = 0; index < 20; index += 1) {
      if (reconnectStates.at(-1)?.retryAttempt === 2) break;
      await new Promise<void>((resolve) => setImmediate(resolve));
    }

    assert.equal(startCount, 4);
    assert.deepEqual(reconnectStates.slice(-2), [
      { status: "reconnecting", retryAttempt: 1, retryDelayMs: 0 },
      { status: "reconnecting", retryAttempt: 2, retryDelayMs: 2_200 },
    ]);

    const restartedAndContinuedCycle = client.retryConnection();
    await Promise.all([restartedCycle, restartedAndContinuedCycle]);
    assert.equal(startCount, 5);
    assert.equal(reconnectedCount, 2);
  } finally {
    await client.dispose();
    HubConnectionBuilder.prototype.build = originalBuild;
  }
});

test("durable attachment detection connects only for a valid persisted execution", async () => {
  const { getDurableExecutionStorageKey, hasPersistedDurableExecution } = await import(
    "./execution-hub" + ".ts"
  );
  const values = new Map<string, string>();
  const originalDescriptor = Object.getOwnPropertyDescriptor(globalThis, "localStorage");
  Object.defineProperty(globalThis, "localStorage", {
    configurable: true,
    value: {
      getItem: (key: string) => values.get(key) ?? null,
      setItem: (key: string, value: string) => values.set(key, value),
      removeItem: (key: string) => values.delete(key),
    },
  });

  try {
    const runtime = { baseUrl: "https://agw.example.test", token: null };
    const setting = { projectId: "project-1", contextId: "context-1" };
    assert.equal(hasPersistedDurableExecution(setting, runtime), false);

    const key = getDurableExecutionStorageKey(runtime, setting);
    values.set(key, JSON.stringify({ executionId: "", cursor: "1-0" }));
    assert.equal(hasPersistedDurableExecution(setting, runtime), false);

    values.set(key, JSON.stringify({ executionId: "execution-1", cursor: "3-9" }));
    assert.equal(hasPersistedDurableExecution(setting, runtime), true);
  } finally {
    if (originalDescriptor) {
      Object.defineProperty(globalThis, "localStorage", originalDescriptor);
    } else {
      Reflect.deleteProperty(globalThis, "localStorage");
    }
  }
});

test("durable configure resumes its cursor, clears 404, and preserves temporary failures", async () => {
  const { ExecutionHubClient, getDurableExecutionStorageKey, hasPersistedDurableExecution } =
    await import("./execution-hub" + ".ts");
  const values = new Map<string, string>();
  const originalStorageDescriptor = Object.getOwnPropertyDescriptor(globalThis, "localStorage");
  const originalBuild = HubConnectionBuilder.prototype.build;
  Object.defineProperty(globalThis, "localStorage", {
    configurable: true,
    value: {
      getItem: (key: string) => values.get(key) ?? null,
      setItem: (key: string, value: string) => values.set(key, value),
      removeItem: (key: string) => values.delete(key),
    },
  });

  const runtime = { baseUrl: "https://agw.example.test", token: null };
  const setting = { projectId: "project-1", contextId: "context-1" };
  const storageKey = getDurableExecutionStorageKey(runtime, setting);

  const createConnection = (
    subscribe: (
      command: { executionId: string; cursor?: string },
      emitMessage: (message: unknown) => void,
    ) => Promise<void>,
  ) => {
    const commands: Array<{ type: string; executionId?: string; cursor?: string }> = [];
    let receiveMessage: ((message: unknown) => void) | undefined;
    const connection = {
      state: HubConnectionState.Disconnected,
      on: (eventName: string, handler: (message: unknown) => void) => {
        if (eventName === "ReceiveMessage") receiveMessage = handler;
      },
      onreconnecting: () => undefined,
      onclose: () => undefined,
      onreconnected: () => undefined,
      start: async () => {
        connection.state = HubConnectionState.Connected;
      },
      stop: async () => {
        connection.state = HubConnectionState.Disconnected;
      },
      invoke: async (methodName: string, command?: { type: string }) => {
        if (methodName === "GetExecutionProvider") return "distributed";
        if (command) commands.push(command);
        if (command?.type === "SubscribeExecutionCommand") {
          await subscribe(command, (message) => receiveMessage?.(message));
        }
      },
    };
    return { connection, commands };
  };

  try {
    values.set(storageKey, JSON.stringify({ executionId: "execution-1", cursor: "3-9" }));
    const resumed = createConnection(async () => undefined);
    HubConnectionBuilder.prototype.build = () => resumed.connection as never;
    const resumedClient = new ExecutionHubClient({ onMessage: () => undefined }, runtime);

    assert.deepEqual(await resumedClient.configure(setting), {
      restoredDurableExecution: true,
    });
    assert.deepEqual(resumed.commands.at(-1), {
      type: "SubscribeExecutionCommand",
      executionId: "execution-1",
      cursor: "3-9",
    });
    await resumedClient.dispose();

    values.set(storageKey, JSON.stringify({ executionId: "execution-terminal", cursor: "3-10" }));
    const terminal = createConnection(async (_command, emitMessage) => {
      emitMessage({
        messageId: "terminal-1",
        role: "system",
        author: "$agw",
        contents: [],
        additionalProperties: {
          type: "turn-finished",
          status: "completed",
          executionId: "execution-terminal",
          streamCursor: "3-11",
        },
      });
    });
    HubConnectionBuilder.prototype.build = () => terminal.connection as never;
    const terminalClient = new ExecutionHubClient({ onMessage: () => undefined }, runtime);

    assert.deepEqual(await terminalClient.configure(setting), {
      restoredDurableExecution: false,
    });
    assert.equal(hasPersistedDurableExecution(setting, runtime), false);
    await terminalClient.dispose();

    values.set(storageKey, JSON.stringify({ executionId: "execution-2", cursor: "4-0" }));
    const missing = createConnection(async () => {
      throw new Error("404_0011: execution not found");
    });
    HubConnectionBuilder.prototype.build = () => missing.connection as never;
    const missingClient = new ExecutionHubClient({ onMessage: () => undefined }, runtime);

    assert.deepEqual(await missingClient.configure(setting), {
      restoredDurableExecution: false,
    });
    assert.equal(hasPersistedDurableExecution(setting, runtime), false);
    await missingClient.dispose();

    values.set(storageKey, JSON.stringify({ executionId: "execution-3", cursor: "5-1" }));
    const temporary = createConnection(async () => {
      throw new Error("temporary transport failure");
    });
    HubConnectionBuilder.prototype.build = () => temporary.connection as never;
    let reconnectState: { status: string } | undefined;
    const temporaryClient = new ExecutionHubClient(
      {
        onMessage: () => undefined,
        onReconnectFailed: (state) => {
          reconnectState = state;
        },
      },
      runtime,
    );

    await assert.rejects(temporaryClient.configure(setting), /temporary transport failure/);
    assert.equal(reconnectState?.status, "failed");
    assert.equal(hasPersistedDurableExecution(setting, runtime), true);
    await temporaryClient.dispose();
  } finally {
    HubConnectionBuilder.prototype.build = originalBuild;
    if (originalStorageDescriptor) {
      Object.defineProperty(globalThis, "localStorage", originalStorageDescriptor);
    } else {
      Reflect.deleteProperty(globalThis, "localStorage");
    }
  }
});

test("getTurnFinishedStatus reads terminal AgwMessage", async () => {
  const { getTurnFinishedStatus } = await import("./execution-hub" + ".ts");

  assert.equal(
    getTurnFinishedStatus({
      messageId: "message-1",
      role: "system",
      author: "$agw",
      contents: [],
      additionalProperties: { type: "turn-finished", status: "interrupted" },
    }),
    "interrupted",
  );
  assert.equal(
    getTurnFinishedStatus({
      messageId: "message-2",
      role: "assistant",
      author: "agent",
      contents: [],
    }),
    null,
  );
});

test("getPendingInteraction parses a structured question interaction", async () => {
  const { getPendingInteraction } = await import("./execution-hub" + ".ts");

  const request = getPendingInteraction({
    messageId: "interaction-message-1",
    role: "system",
    author: "Agw",
    contents: [{ type: "TextContent", content: "Input needed" }],
    additionalProperties: {
      type: "interaction-request",
      interaction: {
        kind: "user-input",
        interactionId: "interaction-1",
        source: { toolName: "ask_user_question", callId: "call-1" },
        prompt: "Choose before continuing.",
        inputKind: "questions",
        payload: {
          questions: [
            {
              question: "Which database?",
              header: "Database",
              multiSelect: false,
              options: [
                { label: "PostgreSQL", description: "Use the production database." },
                { label: "SQLite", description: "Use a local database." },
              ],
            },
          ],
        },
      },
      streamingScopeId: "user-message-1",
    },
  });

  assert.deepEqual(request, {
    kind: "user-input",
    interactionId: "interaction-1",
    source: { toolName: "ask_user_question", callId: "call-1" },
    prompt: "Choose before continuing.",
    inputKind: "questions",
    payload: {
      questions: [
        {
          question: "Which database?",
          header: "Database",
          multiSelect: false,
          options: [
            { label: "PostgreSQL", description: "Use the production database." },
            { label: "SQLite", description: "Use a local database." },
          ],
        },
      ],
    },
    questions: [
      {
        question: "Which database?",
        header: "Database",
        multiSelect: false,
        options: [
          { label: "PostgreSQL", description: "Use the production database." },
          { label: "SQLite", description: "Use a local database." },
        ],
      },
    ],
    streamingScopeId: "user-message-1",
  });
});

test("getMessageStreamingScopeId keeps a restored turn bound to its original user message", async () => {
  const { getMessageStreamingScopeId } = await import("./execution-hub" + ".ts");

  assert.equal(
    getMessageStreamingScopeId({
      messageId: "server-b-turn-start",
      role: "system",
      author: "$agw-server",
      contents: [],
      additionalProperties: {
        type: "turn-start",
        executionId: "execution-1",
        streamingScopeId: "user-message-1",
      },
    }),
    "user-message-1",
  );
});

test("getPendingInteraction parses a mode change interaction", async () => {
  const { getPendingInteraction } = await import("./execution-hub" + ".ts");

  const request = getPendingInteraction({
    messageId: "mode-interaction-message-1",
    role: "system",
    author: "Agw",
    contents: [{ type: "TextContent", content: "Confirm mode change" }],
    additionalProperties: {
      type: "interaction-request",
      interaction: {
        kind: "user-input",
        interactionId: "mode-interaction-1",
        source: { toolName: "mode_set", callId: "mode-call-1" },
        prompt: "The agent wants to switch to Execute mode.",
        inputKind: "mode-change",
        payload: { mode: "execute" },
      },
    },
  });

  assert.deepEqual(request, {
    kind: "user-input",
    interactionId: "mode-interaction-1",
    source: { toolName: "mode_set", callId: "mode-call-1" },
    prompt: "The agent wants to switch to Execute mode.",
    inputKind: "mode-change",
    payload: { mode: "execute" },
    modeChange: { mode: "execute" },
  });
});

test("waitForExecutionTerminal times out when execution never emits a terminal message", async () => {
  const { waitForExecutionTerminal } = await import("./execution-hub" + ".ts");

  await assert.rejects(
    waitForExecutionTerminal(Promise.resolve(), new Promise<void>(() => undefined), 5),
    /Timed out waiting for execution to stop/,
  );
});

test("buildExecutionHubOptions uses the selected desktop Server and token", async () => {
  const { buildExecutionHubOptions } = await import("./execution-hub" + ".ts");

  const result = buildExecutionHubOptions({
    baseUrl: "https://agw.example.test/",
    token: "agw_remote-token",
  });

  assert.equal(result.url, "https://agw.example.test/api/hubs/exec");
  assert.equal(await result.options.accessTokenFactory?.(), "agw_remote-token");
  assert.equal(result.options.skipNegotiation, true);
  assert.equal(result.options.withCredentials, false);
});

test("execution reconnect uses the configured retry schedule and then stops", async (t) => {
  t.mock.method(Math, "random", () => 0);
  const { executionReconnectDelaysMs, getExecutionReconnectDelay, isExecutionReconnectExhausted } =
    await import("./execution-hub" + ".ts");

  assert.deepEqual(
    [...executionReconnectDelaysMs],
    [1_000, 2_000, 3_000, 5_000, 8_000, 13_000, 21_000, 34_000, 55_000, 60_000],
  );
  assert.deepEqual(
    Array.from({ length: executionReconnectDelaysMs.length + 1 }, (_, retryCount) =>
      getExecutionReconnectDelay(retryCount),
    ),
    [1_000, 2_000, 3_000, 5_000, 8_000, 13_000, 21_000, 34_000, 55_000, 60_000, null],
  );
  assert.equal(
    isExecutionReconnectExhausted({
      status: "reconnecting",
      retryAttempt: executionReconnectDelaysMs.length,
      retryDelayMs: 0,
    }),
    true,
  );
  assert.equal(
    isExecutionReconnectExhausted({
      status: "failed",
      retryAttempt: executionReconnectDelaysMs.length,
      retryDelayMs: 0,
    }),
    false,
  );
});

test("in-process reconnect keeps the old turn busy until the server confirms it has stopped", async (t) => {
  const { ExecutionSession } = await import("./execution-session.ts");
  const { ExecutionSessionManager } = await import("./execution-session-manager.ts");
  t.mock.timers.enable({ apis: ["setTimeout"] });
  let receive!: (message: AiMessage) => void;
  let reconnect!: () => void;
  let reconnected!: () => void;
  let close!: (error?: Error) => void;
  let active = true;
  let dispatchError: Error | null = null;
  let recoveryError: Error | null = null;
  const recoveryCalls: unknown[][] = [];
  const connection = {
    connectionId: "old-connection",
    state: HubConnectionState.Disconnected,
    on: (_event: string, handler: typeof receive) => {
      receive = handler;
    },
    onreconnecting: (handler: typeof reconnect) => {
      reconnect = handler;
    },
    onreconnected: (handler: typeof reconnected) => {
      reconnected = handler;
    },
    onclose: (handler: typeof close) => {
      close = handler;
    },
    start: async () => {
      connection.state = HubConnectionState.Connected;
    },
    stop: async () => {
      connection.state = HubConnectionState.Disconnected;
    },
    invoke: async (method: string, ...args: unknown[]) => {
      if (method === "GetExecutionProvider") return "InProcess";
      if (method === "FindInProcessExecution") return null;
      if (method === "RecoverInProcessExecution") {
        recoveryCalls.push(args);
        if (recoveryError) throw recoveryError;
        return active;
      }
      if (
        method === "DispatchCommand" &&
        (args[0] as { type: string }).type === "ExecCommand" &&
        dispatchError
      ) {
        throw dispatchError;
      }
    },
  };
  t.mock.method(HubConnectionBuilder.prototype, "build", () => connection as never);
  const manager = new ExecutionSessionManager(
    (handlers) =>
      new ExecutionSession(handlers, {
        baseUrl: "https://agw.test",
        token: null,
        attachmentStore: null,
      }),
  );
  const key = { serverId: "server", projectId: "project", contextId: "context" };
  const handle = manager.attach(key, { onMessage: () => undefined });
  const request = {
    conversationId: "conversation",
    agentId: "agent",
    agentType: 0 as const,
    input: { messageId: "input", author: "$agw", contents: [] },
  };
  const settle = async () => {
    for (let i = 0; i < 20; i++) await Promise.resolve();
  };
  try {
    await handle.configure({ projectId: "project", contextId: "context" });
    await handle.execute(request);
    receive({
      messageId: "error",
      role: "system",
      contents: [{ type: "ErrorContent", content: "Reconnecting... waiting for network" }],
    });
    assert.equal(handle.getStatus(), "running");
    connection.state = HubConnectionState.Reconnecting;
    reconnect();
    connection.connectionId = "new-connection";
    connection.state = HubConnectionState.Connected;
    reconnected();
    await settle();
    assert.equal(handle.getStatus(), "running");
    assert.deepEqual(recoveryCalls.at(-1), ["old-connection", false]);
    await assert.rejects(handle.execute(request), /already has a running task/);

    connection.state = HubConnectionState.Disconnected;
    close(new Error("network lost again"));
    assert.equal(handle.getReconnectState()?.status, "failed");
    connection.connectionId = "third-connection";
    await manager.retryConnection(key);
    assert.equal(handle.getStatus(), "running");
    assert.deepEqual(recoveryCalls.at(-1), ["old-connection", false]);
    await handle.interrupt();
    assert.deepEqual(recoveryCalls.at(-1), ["old-connection", true]);
    assert.equal(handle.getStatus(), "running");

    active = false;
    t.mock.timers.tick(1000);
    await settle();
    assert.equal(handle.getStatus(), "idle");
    active = true;
    dispatchError = new Error("Startup response lost");
    await assert.rejects(
      handle.execute({ ...request, input: { ...request.input, messageId: "next-input" } }),
      /Startup response lost/,
    );
    assert.equal(handle.getStatus(), "running");
    assert.deepEqual(recoveryCalls.at(-1), ["third-connection", false]);

    recoveryError = new Error("Recovery unavailable");
    t.mock.timers.tick(1000);
    await settle();
    assert.equal(handle.getStatus(), "running");
    assert.equal(handle.getReconnectState()?.status, "failed");
    recoveryError = null;
    active = false;
    await manager.retryConnection(key);
    assert.equal(handle.getStatus(), "idle");
    dispatchError = new Error("HubException: 4000001: Invalid request");
    await assert.rejects(handle.execute(request), /Invalid request/);
    assert.equal(handle.getStatus(), "idle");
  } finally {
    await handle.dispose();
  }
});

test("a fresh client discovers an in-process turn without local attachment storage", async (t) => {
  const { ExecutionSession } = await import("./execution-session.ts");
  const { ExecutionSessionManager } = await import("./execution-session-manager.ts");
  const calls: { method: string; args: unknown[] }[] = [];
  let discoveryError = false;
  let active = true;
  const connection = {
    connectionId: "after-reload",
    state: HubConnectionState.Disconnected,
    on() {},
    onclose() {},
    onreconnecting() {},
    onreconnected() {},
    start: async () => {
      connection.state = HubConnectionState.Connected;
    },
    stop: async () => {
      connection.state = HubConnectionState.Disconnected;
    },
    invoke: async (method: string, ...args: unknown[]) => {
      calls.push({ method, args });
      if (method === "GetExecutionProvider") return "InProcess";
      if (method === "FindInProcessExecution") {
        if (discoveryError) throw new Error("State query unavailable");
        return active ? "before-reload" : null;
      }
      if (method === "RecoverInProcessExecution") return active;
    },
  };
  t.mock.method(HubConnectionBuilder.prototype, "build", () => connection as never);
  const manager = new ExecutionSessionManager(
    (handlers) =>
      new ExecutionSession(handlers, {
        baseUrl: "https://agw.test",
        token: null,
        attachmentStore: null,
      }),
  );
  const key = { serverId: "server", projectId: "project", contextId: "context" };
  const handle = manager.attach(key, { onMessage() {} });
  try {
    await handle.configure({ projectId: key.projectId, contextId: key.contextId });
    assert.equal(handle.getStatus(), "running");
    assert.deepEqual(calls.find((call) => call.method === "FindInProcessExecution")?.args, [
      "project",
      "context",
    ]);
    await assert.rejects(
      handle.execute({
        conversationId: "conversation",
        agentId: "agent",
        agentType: 0,
        input: { messageId: "input", author: "$agw", contents: [] },
      }),
      /already has a running task/,
    );
    await handle.interrupt();
    assert.deepEqual(calls.at(-1), {
      method: "RecoverInProcessExecution",
      args: ["before-reload", true],
    });
  } finally {
    await handle.dispose();
  }

  const fresh = manager.attach(key, { onMessage() {} });
  try {
    discoveryError = true;
    await assert.rejects(
      fresh.configure({ projectId: key.projectId, contextId: key.contextId }),
      /State query unavailable/,
    );
    assert.equal(fresh.getReconnectState()?.status, "failed");
    discoveryError = false;
    await manager.retryConnection(key);
    assert.equal(fresh.getStatus(), "running");
    active = false;
    await fresh.interrupt();
    assert.equal(fresh.getStatus(), "idle");
  } finally {
    await fresh.dispose();
  }
});

for (const disconnected of [false, true]) {
  test(`an unacknowledged WebSocket-only start stays busy until recovery confirms idle (disconnected: ${disconnected})`, async (t) => {
    const { ExecutionSession } = await import("./execution-session.ts");
    let serverActive = false;
    let reconnected!: () => void;
    const recoveryCalls: unknown[][] = [];
    const connection = {
      connectionId: null,
      state: HubConnectionState.Disconnected,
      on() {},
      onclose() {},
      onreconnecting() {},
      onreconnected(handler: typeof reconnected) {
        reconnected = handler;
      },
      async start() {
        connection.state = HubConnectionState.Connected;
      },
      async stop() {
        connection.state = HubConnectionState.Disconnected;
      },
      async invoke(method: string, ...args: unknown[]) {
        if (method === "GetExecutionProvider") return "InProcess";
        if (method === "FindInProcessExecution") return serverActive ? "original-connection" : null;
        if (method === "RecoverInProcessExecution") {
          recoveryCalls.push(args);
          if (args[1] === true) serverActive = false;
          return serverActive;
        }
        if (method === "DispatchCommand" && (args[0] as { type: string }).type === "ExecCommand") {
          serverActive = true;
          if (disconnected) connection.state = HubConnectionState.Disconnected;
          throw new Error("Connection lost before the execution acknowledgement.");
        }
      },
    };
    t.mock.method(HubConnectionBuilder.prototype, "build", () => connection as never);
    const session = new ExecutionSession(
      { onMessage() {} },
      {
        baseUrl: "https://agw.test",
        token: null,
        attachmentStore: null,
      },
    );
    try {
      await session.configure({ projectId: "project", contextId: "context" });
      await assert.rejects(
        session.execute({
          conversationId: "conversation",
          agentId: "agent",
          agentType: 0,
          input: { messageId: "input", author: "$agw", contents: [] },
        }),
        /acknowledgement/,
      );
      assert.equal(serverActive, true);
      assert.equal(session.hasActiveExecution(), true);
      if (disconnected) {
        connection.state = HubConnectionState.Connected;
        reconnected();
        for (let i = 0; i < 40; i++) await Promise.resolve();
      }
      assert.deepEqual(recoveryCalls, [["original-connection", false]]);
      await session.interrupt();
      assert.deepEqual(recoveryCalls.at(-1), ["original-connection", true]);
      assert.equal(serverActive, false);
      assert.equal(session.hasActiveExecution(), false);
    } finally {
      await session.dispose();
    }
  });
}
