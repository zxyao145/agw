import { expect, type Locator, type Page } from "@playwright/test";
import { readApiResult as apiResult, standaloneHostTest } from "./standalone-host";

export const agentNames = ["agentflow-test-writer", "agentflow-test-reviewer"] as const;

export type AgentflowDefinition = {
  nodes: Record<string, unknown>[];
  edges: Record<string, unknown>[];
};
type AgentflowDetail = {
  agentflow: AgentflowDto;
  definition: AgentflowDefinition;
  revision: number;
};
type AgentflowDto = { id: string; name: string; latestRevision: number; publishedRevision: number };

export const agentflowTest = standaloneHostTest.extend<{
  agentIds: string[];
  requestToken: string;
}>({
  requestToken: async ({ page, standaloneHost }, use) => {
    const { requestToken } = await apiResult<{ requestToken: string }>(
      await page.request.get(`${standaloneHost.origin}/api/auth/antiforgery`),
    );
    await use(requestToken);
  },
  agentIds: async ({ page, standaloneHost, requestToken }, use) => {
    const agentIds = [];
    for (const name of agentNames) {
      const agent = await apiResult<{ id: string }>(
        await page.request.post(`${standaloneHost.origin}/api/agents`, {
          headers: { "X-CSRF-TOKEN": requestToken },
          data: {
            name,
            displayName: name,
            systemPrompt: "Test agent.",
            description: "Test agent.",
            type: 1,
            externalAgentKind: 2,
          },
        }),
      );
      agentIds.push(agent.id);
    }
    await use(agentIds);
  },
});

export const graphEdge = (edgeId: string, sourceNodeId: string, targetNodeId: string) => ({
  edgeId,
  sourceNodeId,
  targetNodeId,
  isFanInBarrier: false,
  isSwitchDefault: false,
  switchCaseOrder: 0,
  choiceId: null,
  label: null,
  conditions: [],
});

export function sequentialDefinition(agentId: string): AgentflowDefinition {
  return {
    nodes: [
      {
        nodeId: "input",
        kind: "input",
        name: "Input",
        routingStrategy: "direct",
        position: { x: 80, y: 80 },
      },
      {
        nodeId: "draft",
        kind: "agent",
        name: "Draft",
        routingStrategy: "direct",
        position: { x: 400, y: 80 },
        relateId: agentId,
        instructions: "",
      },
      {
        nodeId: "output",
        kind: "output",
        name: "Output",
        routingStrategy: "direct",
        position: { x: 720, y: 80 },
        enableSummary: false,
        instructions: null,
        summaryModelProviderId: null,
      },
    ],
    edges: [graphEdge("to-draft", "input", "draft"), graphEdge("to-output", "draft", "output")],
  };
}

export async function createFlow(
  page: Page,
  origin: string,
  requestToken: string,
  name: string,
  definition: AgentflowDefinition,
): Promise<AgentflowDto> {
  return apiResult(
    await page.request.post(`${origin}/api/agentflows`, {
      headers: { "X-CSRF-TOKEN": requestToken },
      data: { name, description: null, definition },
    }),
  );
}

export async function publishFlow(
  page: Page,
  origin: string,
  requestToken: string,
  agentflowId: string,
  revision: number,
): Promise<void> {
  await apiResult(
    await page.request.put(`${origin}/api/agentflows/published`, {
      headers: { "X-CSRF-TOKEN": requestToken },
      data: { agentflowId, revision },
    }),
  );
}

export async function createAgent(
  page: Page,
  origin: string,
  requestToken: string,
  name: string,
): Promise<{ id: string }> {
  return apiResult(
    await page.request.post(`${origin}/api/agents`, {
      headers: { "X-CSRF-TOKEN": requestToken },
      data: {
        name,
        displayName: name,
        systemPrompt: "Test agent.",
        description: "Test configuration.",
        type: 1,
        externalAgentKind: 2,
      },
    }),
  );
}

export async function readFlow(
  page: Page,
  origin: string,
  id: string,
  revision: number,
): Promise<AgentflowDetail> {
  return apiResult(
    await page.request.get(`${origin}/api/agentflows/detail?id=${id}&revision=${revision}`),
  );
}

export async function openEditor(page: Page, flowName?: string): Promise<Locator> {
  await page.goto("/agentflows");
  if (flowName) {
    await page
      .getByRole("row")
      .filter({ hasText: flowName })
      .getByTitle("Edit agentflow", { exact: true })
      .click();
  } else {
    await page.getByRole("button", { name: "Create", exact: true }).click();
  }
  const dialog = page.getByRole("dialog", {
    name: flowName ? "Edit Agentflow" : "Create Agentflow",
  });
  await expect(dialog).toBeVisible();
  return dialog;
}

export function canvasNode(dialog: Locator, name: string): Locator {
  return dialog
    .locator(".react-flow__node")
    .filter({ has: dialog.page().getByText(name, { exact: true }) });
}

export function flowNameInput(dialog: Locator): Locator {
  return dialog.getByPlaceholder("Untitled Agentflow", { exact: true });
}
