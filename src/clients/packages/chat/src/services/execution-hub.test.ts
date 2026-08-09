import assert from "node:assert/strict";
import test from "node:test";

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
  assert.equal(result.options.withCredentials, false);
});
