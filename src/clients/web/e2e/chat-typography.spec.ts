import { expect, test, type Page } from "@playwright/test";

const projectId = "11111111-1111-4111-8111-111111111111";
const conversationId = "22222222-2222-4222-8222-222222222222";
const turnId = "33333333-3333-4333-8333-333333333333";

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
  jobId: null,
  title: "Typography",
  latestStatus: null,
  executionCount: 1,
  messageCount: 2,
  createTime: "2026-01-01T00:00:00Z",
  updateTime: null,
  errorMessage: null,
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

async function mockChatApi(page: Page) {
  await page.route("**/api/**", async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (request.method() !== "GET")
      throw new Error(`Unexpected mutation: ${request.method()} ${path}`);
    const conversationPath = `/api/projects/${projectId}/conversations`;
    const responses: Record<string, unknown> = {
      "/api/auth/session": {
        authenticated: true,
        accessMode: "cookie",
        apiMajorVersion: 1,
        userId: "1001",
      },
      "/api/auth/antiforgery": { requestToken: "test-csrf" },
      "/api/agents": [],
      "/api/agentflows": [],
      "/api/quick-prompts": [],
      "/api/projects": [project],
      "/api/projects/conversation-activity": [],
      [conversationPath]: { items: [conversation], total: 1, pageIndex: 1, pageSize: 20 },
      [`${conversationPath}/${conversationId}`]: conversation,
      [`${conversationPath}/${conversationId}/messages`]: {
        items: [userMessage, assistantMessage],
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
    await route.fulfill({ json: { code: 0, title: "OK", data: responses[path] } });
  });
}

test("chat Markdown uses the compact typography of the shared prose", async ({ page }) => {
  await mockChatApi(page);
  await page.goto(`/chat/?projectId=${projectId}&conversationId=${conversationId}`);
  const prose = page.locator('.agw-prose[data-density="compact"]', { hasText: "Body with" });
  await expect(prose.locator("h1")).toHaveText("Heading");

  const fontSize = (selector: string) =>
    prose.locator(selector).evaluate((element) => getComputedStyle(element).fontSize);
  expect(await fontSize("p")).toBe("14px");
  expect(await fontSize("h1")).toBe("24.5px");
  expect(await fontSize("p code")).toBe("14px");
});
