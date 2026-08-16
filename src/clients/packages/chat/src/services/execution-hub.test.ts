import assert from "node:assert/strict";
import test from "node:test";
import { HubConnectionBuilder, HubConnectionState } from "@microsoft/signalr";

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

  assert.deepEqual(buildExecCommand({ agentId: "agent-1", agentType: 0, stream: false, input }), {
    type: "ExecCommand",
    agentId: "agent-1",
    agentType: 0,
    stream: false,
    input,
  });
});

test("buildExecCommand includes a durable execution identity when supplied", async () => {
  const { buildExecCommand } = await import("./execution-hub" + ".ts");
  const input = { messageId: "message-1", author: "$agw", contents: [] };

  assert.deepEqual(
    buildExecCommand({
      executionId: "execution-1",
      agentId: "agent-1",
      agentType: 0,
      input,
    }),
    {
      type: "ExecCommand",
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

test("getPendingHumanGate parses a structured question interaction", async () => {
  const { getPendingHumanGate } = await import("./execution-hub" + ".ts");

  const request = getPendingHumanGate({
    messageId: "interaction-message-1",
    role: "system",
    author: "Agw",
    contents: [{ type: "TextContent", content: "Input needed" }],
    additionalProperties: {
      type: "human-interaction-request",
      requestId: "interaction-1",
      interactionKind: "questions",
      toolName: "ask_user_question",
      callId: "call-1",
      streamingScopeId: "user-message-1",
      prompt: "Choose before continuing.",
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
  });

  assert.deepEqual(request, {
    requestType: "human-interaction",
    requestId: "interaction-1",
    mode: "interaction",
    interactionKind: "questions",
    toolName: "ask_user_question",
    callId: "call-1",
    streamingScopeId: "user-message-1",
    prompt: "Choose before continuing.",
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

test("getPendingHumanGate parses a mode change interaction", async () => {
  const { getPendingHumanGate } = await import("./execution-hub" + ".ts");

  const request = getPendingHumanGate({
    messageId: "mode-interaction-message-1",
    role: "system",
    author: "Agw",
    contents: [{ type: "TextContent", content: "Confirm mode change" }],
    additionalProperties: {
      type: "human-interaction-request",
      requestId: "mode-interaction-1",
      interactionKind: "mode-change",
      toolName: "mode_set",
      callId: "mode-call-1",
      prompt: "The agent wants to switch to Execute mode.",
      payload: { mode: "execute" },
    },
  });

  assert.deepEqual(request, {
    requestType: "human-interaction",
    requestId: "mode-interaction-1",
    mode: "interaction",
    interactionKind: "mode-change",
    toolName: "mode_set",
    callId: "mode-call-1",
    prompt: "The agent wants to switch to Execute mode.",
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

test("execution reconnect uses the configured retry schedule and then stops", async () => {
  const { executionReconnectDelaysMs, getExecutionReconnectDelay, isExecutionReconnectExhausted } =
    await import("./execution-hub" + ".ts");

  assert.deepEqual(
    [...executionReconnectDelaysMs],
    [0, 2_000, 5_000, 7_000, 10_000, 20_000, 30_000],
  );
  assert.deepEqual(
    Array.from({ length: executionReconnectDelaysMs.length + 1 }, (_, retryCount) =>
      getExecutionReconnectDelay(retryCount),
    ),
    [0, 2_000, 5_000, 7_000, 10_000, 20_000, 30_000, null],
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
