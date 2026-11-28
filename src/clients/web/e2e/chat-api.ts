import type { Page } from "@playwright/test";
import { fulfillApiData, fulfillShellRead } from "./web-session-api";

export const projectId = "11111111-1111-4111-8111-111111111111";
export const conversationId = "22222222-2222-4222-8222-222222222222";
const turnId = "33333333-3333-4333-8333-333333333333";
const agentflowId = "44444444-4444-4444-8444-444444444444";

const project = {
  id: projectId,
  name: "Project",
  type: 0,
  description: null,
  workspace: null,
  extraSetting: null,
  tools: [],
  environmentVariables: {},
  projectMcpToolServers: [],
  projectSkillRelations: [],
  projectConnectionRelations: [],
  createTime: "2026-01-01T00:00:00Z",
  createBy: null,
  updateTime: null,
  updateBy: null,
};

const conversation = {
  projectId,
  conversationId,
  contextId: "typography-context",
  kind: "native",
  relateId: null,
  title: "Typography",
  latestStatus: null,
  executionCount: 1,
  messageCount: 2,
  createTime: "2026-01-01T00:00:00Z",
  updateTime: null,
  errorMessage: null,
};

// 对话详情在列表项之外还有用量与恢复状态。The conversation details add usage and resume state to the list item.
const conversationDetails = {
  ...conversation,
  usage: {
    inputTokenCount: 0,
    outputTokenCount: 0,
    totalTokenCount: 0,
    cachedInputTokenCount: 0,
    reasoningTokenCount: 0,
    unsettledTurnIds: [],
  },
  resumeState: null,
};

const textMessage = (messageId: string, role: string, text: string) => ({
  messageId,
  createdAt: "2026-01-01T00:00:00Z",
  author: role,
  role,
  contents: [{ type: "TextContent", content: text }],
});

const userMessage = textMessage("user-1", "user", "Show typography");
const assistantMessage = textMessage("assistant-1", "assistant", "# Heading\n\nBody with `code`.");

const agentflow = {
  id: agentflowId,
  name: "Draft workflow",
  description: null,
  enable: true,
  latestRevision: 1,
  publishedRevision: 1,
  createTime: conversation.createTime,
  updateTime: conversation.createTime,
};

const nodeId = (index: number) => `node-${index}`;
const agentflowDefinition = {
  nodes: Array.from({ length: 40 }, (_, index) => ({
    kind: index === 0 ? "input" : "promptAdapter",
    nodeId: nodeId(index),
    name: index === 0 ? null : `Prepare draft ${index}`,
    position: { x: index, y: 0 },
    routingStrategy: "broadcast",
    instructions: null,
  })),
  edges: Array.from({ length: 39 }, (_, index) => ({
    edgeId: `edge-${index}`,
    sourceNodeId: nodeId(index),
    targetNodeId: nodeId(index + 1),
    isFanInBarrier: false,
    isSwitchDefault: false,
    switchCaseOrder: 0,
    choiceId: null,
    label: null,
    conditions: [],
  })),
};

const todoSnapshot = {
  ...textMessage("todo-snapshot", "system", ""),
  additionalProperties: {
    type: "tool-todo-snapshot",
    callId: "todo-call",
    toolName: "todos_add",
    items: [{ id: 1, title: "Prepare the draft", isComplete: false }],
  },
};

export async function mockChatApi(page: Page, { withAgentflow = false } = {}) {
  await page.route("**/api/**", async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (path.startsWith("/api/hubs/")) return route.abort();
    if (request.method() !== "GET")
      throw new Error(`Unexpected mutation: ${request.method()} ${path}`);
    const shellResponse = fulfillShellRead(route, path);
    if (shellResponse) return shellResponse;
    const conversationPath = `/api/projects/${projectId}/conversations`;
    const responses: Record<string, unknown> = {
      "/api/agents": [],
      "/api/agentflows": withAgentflow ? [agentflow] : [],
      "/api/agentflows/detail": { agentflow, revision: 1, definition: agentflowDefinition },
      "/api/agents/input-capabilities": { supportsSteer: false },
      "/api/agents/permission-capabilities": { supportedPermissionModes: ["fullAccess"] },
      "/api/quick-prompts": [],
      "/api/projects": [project],
      "/api/projects/conversation-activity": [],
      [conversationPath]: { items: [conversation], total: 1, pageIndex: 1, pageSize: 20 },
      [`${conversationPath}/${conversationId}`]: withAgentflow
        ? {
            ...conversationDetails,
            resumeState: { targetType: "agentflow", targetId: agentflowId, agentMode: null },
          }
        : conversationDetails,
      [`${conversationPath}/${conversationId}/messages`]: {
        items: withAgentflow
          ? [userMessage, todoSnapshot, assistantMessage]
          : [userMessage, assistantMessage],
        nextCursor: null,
        hasMore: false,
        turns: [
          {
            turnId,
            status: "completed",
            input: userMessage,
            results: [assistantMessage],
            hasProcessMessages: false,
          },
        ],
      },
    };
    if (!(path in responses)) throw new Error(`Unexpected query: ${path}`);
    return fulfillApiData(route, responses[path]);
  });
}
