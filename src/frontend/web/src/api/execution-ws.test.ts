import assert from "node:assert/strict";
import test from "node:test";

test("buildSettingCommandPayload includes environment variables", async () => {
  const { buildSettingCommandPayload } = await import("./execution-ws" + ".ts");

  const payload = buildSettingCommandPayload({
    projectId: "project-1",
    contextId: "context-1",
    environmentVariables: {
      AGW_TOKEN: "secret",
    },
  });

  assert.equal(payload.type, "SettingCommand");
  assert.deepEqual(payload.environmentVariables, {
    AGW_TOKEN: "secret",
  });
});

test("buildSettingCommandPayload includes context id", async () => {
  const { buildSettingCommandPayload } = await import("./execution-ws" + ".ts");

  const payload = buildSettingCommandPayload({
    projectId: "project-1",
    contextId: "context-1",
  });

  assert.equal(payload.contextId, "context-1");
  assert.equal(("task" + "Id") in payload, false);
});

test("buildSettingCommandPayload omits optional environment variables when absent", async () => {
  const { buildSettingCommandPayload } = await import("./execution-ws" + ".ts");

  const payload = buildSettingCommandPayload({
    projectId: "project-1",
    contextId: "context-1",
  });

  assert.equal("environmentVariables" in payload, false);
});

test("buildHumanResponseCommandPayload returns HumanResponseCommand", async () => {
  const { buildHumanResponseCommandPayload } = await import("./execution-ws" + ".ts");

  const payload = buildHumanResponseCommandPayload({
    requestId: "human-1",
    approved: true,
    responseText: "continue",
  });

  assert.deepEqual(payload, {
    type: "HumanResponseCommand",
    requestId: "human-1",
    approved: true,
    responseText: "continue",
  });
});

test("getHumanGateRequest returns request metadata", async () => {
  const { getHumanGateRequest } = await import("./execution-ws" + ".ts");

  const request = getHumanGateRequest({
    messageId: "message-1",
    author: "$agw-server",
    role: "system",
    contents: [{ type: "TextContent", content: "Approve?" }],
    additionalProperties: {
      type: "human-gate-request",
      requestId: "human-1",
      nodeId: "gate",
      nodeName: "Review Gate",
      mode: "approval",
      prompt: "Approve?",
    },
  });

  assert.deepEqual(request, {
    requestId: "human-1",
    nodeId: "gate",
    nodeName: "Review Gate",
    mode: "approval",
    prompt: "Approve?",
    inputPreview: undefined,
  });
});
