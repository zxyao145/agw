import { expect } from "@playwright/test";
import {
  agentflowTest,
  agentNames,
  canvasNode,
  createFlow,
  graphEdge,
  openEditor,
  sequentialDefinition,
} from "./agentflow-editor-fixture";
import { readApiResult } from "./standalone-host";

agentflowTest.skip(
  ({ viewport }) => !!viewport && viewport.width < 600,
  "Requires the editor's desktop viewport.",
);

agentflowTest(
  "AgentflowInspector_ChangesAgent_PreservesCustomNameAndUpdatesSyncedName",
  async ({ page, standaloneHost, requestToken, agentIds }) => {
    await createFlow(
      page,
      standaloneHost.origin,
      requestToken,
      "Reference editor",
      sequentialDefinition(agentIds[0]),
    );
    const dialog = await openEditor(page, "Reference editor");
    await canvasNode(dialog, "Draft").click();
    const inspector = dialog.locator("aside").filter({ hasText: "Outgoing" });
    const name = inspector.getByRole("textbox", { name: "Name", exact: true });
    const selectAgent = async (agentName: string) => {
      await inspector.getByRole("combobox", { name: "Agent", exact: true }).click();
      await page.getByRole("option").filter({ hasText: agentName }).click();
    };

    await selectAgent(agentNames[1]);
    await expect(name).toHaveValue("Draft");
    await name.fill(agentNames[1]);
    await selectAgent(agentNames[0]);
    await expect(name).toHaveValue(agentNames[0]);
  },
);

agentflowTest(
  "AgentflowInspector_ConditionChanges_UndoesEachEditSeparately",
  async ({ page, standaloneHost, requestToken, agentIds }) => {
    await createFlow(
      page,
      standaloneHost.origin,
      requestToken,
      "Condition history",
      sequentialDefinition(agentIds[0]),
    );
    const dialog = await openEditor(page, "Condition history");
    await canvasNode(dialog, "Draft").click();
    const inspector = dialog.locator("aside");
    const add = inspector.getByRole("button", { name: "Add condition" }).first();
    const value = inspector.getByRole("textbox", { name: "Condition value" });
    const undo = dialog.getByRole("button", { name: "Undo", exact: true });

    await add.click();
    await value.fill("reviewed");
    await inspector.getByRole("button", { name: "Remove condition" }).click();
    await expect(value).toHaveCount(0);
    await expect(undo).toBeEnabled();
    await undo.click();
    await expect(value).toHaveValue("reviewed");
    await undo.click();
    await expect(value).toHaveValue("");
    await undo.click();
    await expect(value).toHaveCount(0);
  },
);

agentflowTest(
  "AgentflowEditor_ValidationFails_ShowsRetryBeforeSaving",
  async ({ page, standaloneHost, requestToken, agentIds }) => {
    await createFlow(
      page,
      standaloneHost.origin,
      requestToken,
      "Validation recovery",
      sequentialDefinition(agentIds[0]),
    );
    const dialog = await openEditor(page, "Validation recovery");
    await expect(dialog.getByRole("button", { name: "No problems", exact: true })).toBeVisible();
    const routePattern = "**/api/agentflows/validate";
    await page.route(routePattern, (route) => route.abort());
    await dialog.getByRole("textbox", { name: "Agentflow name" }).fill("Updated name");
    await dialog.getByRole("textbox", { name: "Agentflow name" }).blur();
    const failed = dialog.getByRole("button", { name: "Validation failed", exact: true });
    await expect(failed).toBeVisible();
    await expect(dialog.getByRole("button", { name: "Save", exact: true })).toHaveAttribute(
      "title",
      "Retry validation before saving",
    );

    await failed.click();
    await page.unroute(routePattern);
    await dialog.getByRole("button", { name: "Retry", exact: true }).click();
    await expect(dialog.getByRole("button", { name: "Save", exact: true })).toBeEnabled();
  },
);

agentflowTest(
  "AgentflowEditor_RunDraft_QueriesPermissionCapabilitiesForItsRevision",
  async ({ page, standaloneHost, requestToken, agentIds }) => {
    const flow = await createFlow(
      page,
      standaloneHost.origin,
      requestToken,
      "Draft permissions",
      sequentialDefinition(agentIds[0]),
    );
    const [response, dialog] = await Promise.all([
      page.waitForResponse(
        (response) => {
          const url = new URL(response.url());
          return (
            url.pathname === "/api/agents/permission-capabilities" &&
            url.searchParams.get("id") === flow.id
          );
        },
        { timeout: 45_000 },
      ),
      openEditor(page, "Draft permissions"),
    ]);
    await dialog.getByTitle("Run", { exact: true }).click();

    expect(new URL(response.url()).searchParams.get("agentflowRevision")).toBe("1");
    const result = await readApiResult<{ supportedPermissionModes: string[] }>(response);
    expect(result.supportedPermissionModes).toEqual(["fullAccess"]);
    await dialog.getByRole("combobox", { name: "Tool permission mode" }).click();
    await expect(page.getByRole("option", { name: "Full access" })).toBeEnabled();
    await expect(page.getByRole("option", { name: "Always ask" })).toBeDisabled();
  },
);

agentflowTest(
  "AgentflowInspector_GateWithSharedChoice_ShowsEveryEdgeAndAddsBlockMember",
  async ({ page, standaloneHost, requestToken, agentIds }) => {
    const definition = sequentialDefinition(agentIds[0]);
    definition.nodes.push(
      {
        nodeId: "review",
        name: "Review",
        kind: "humanGate",
        routingStrategy: "choice",
        position: { x: 400, y: 260 },
        humanPrompt: "Review the draft.",
        choices: [
          { choiceId: "approve", label: "Approve" },
          { choiceId: "stop", label: "Stop" },
        ],
        allowResponseText: false,
      },
      {
        nodeId: "team",
        name: "Review team",
        kind: "concurrentBlock",
        routingStrategy: "direct",
        position: { x: 720, y: 260 },
        instructions: null,
        members: [
          {
            memberId: "writer",
            kind: "agent",
            relateId: agentIds[0],
            name: agentNames[0],
            instructions: null,
            isManager: false,
          },
        ],
      },
    );
    definition.edges.splice(
      1,
      1,
      graphEdge("to-review", "draft", "review"),
      { ...graphEdge("to-output", "review", "output"), choiceId: "approve" },
      { ...graphEdge("to-team", "review", "team"), choiceId: "approve" },
    );

    await createFlow(page, standaloneHost.origin, requestToken, "Shared choice", definition);
    const dialog = await openEditor(page, "Shared choice");
    await canvasNode(dialog, "Review").click();
    const inspector = dialog.locator("aside").filter({ hasText: "Outgoing" });
    const approved = inspector.locator("div.rounded-md").filter({
      has: page.getByRole("heading", { name: "Approve", exact: true }),
    });
    await expect(approved.getByRole("button", { name: "Output", exact: true })).toBeVisible();
    await expect(approved.getByRole("button", { name: "Review team", exact: true })).toBeVisible();
    await expect(inspector.getByText("This path ends here.")).toBeVisible();

    const choices = inspector.getByRole("textbox", { name: "Choice label" });
    await inspector.getByTitle("Drag choice to reorder").last().dragTo(choices.first());
    await expect(choices.first()).toHaveValue("Stop");
    await expect(approved.getByLabel("Choice path")).toHaveCount(2);
    await expect(approved.getByLabel("Choice path").first()).toHaveValue("approve");
    await expect(approved.getByLabel("Choice path").last()).toHaveValue("approve");

    await approved.getByRole("button", { name: "Review team", exact: true }).click();
    const connection = dialog.locator("aside").filter({ hasText: "Label on the canvas" });
    await connection.getByRole("button", { name: "Review team", exact: true }).click();
    await inspector.getByRole("button", { name: "Add member", exact: true }).click();
    await page
      .locator('[data-slot="popover-content"]')
      .getByRole("button", { name: new RegExp(agentNames[1]) })
      .click();
    const member = dialog.locator("aside").filter({ hasText: "Block member" });
    await expect(member.getByRole("textbox", { name: "Name", exact: true })).toHaveValue(
      agentNames[1],
    );
  },
);

agentflowTest(
  "AgentflowInspector_BranchWithWaitAndElse_OpensConnectionsWithoutConditionForms",
  async ({ page, standaloneHost, requestToken, agentIds }) => {
    const definition = sequentialDefinition(agentIds[0]);
    definition.nodes[1].routingStrategy = "switch";
    definition.nodes.push(
      {
        nodeId: "wait-target",
        name: "Wait target",
        kind: "clearMessages",
        routingStrategy: "direct",
        position: { x: 720, y: 260 },
      },
      {
        nodeId: "condition-target",
        name: "Matched branch",
        kind: "clearMessages",
        routingStrategy: "direct",
        position: { x: 720, y: 440 },
      },
      {
        nodeId: "other-target",
        name: "Other branch",
        kind: "clearMessages",
        routingStrategy: "direct",
        position: { x: 1000, y: 440 },
      },
    );
    definition.edges[1].isSwitchDefault = true;
    definition.edges.push(
      {
        ...graphEdge("to-wait", "draft", "wait-target"),
        isFanInBarrier: true,
      },
      {
        ...graphEdge("to-condition", "draft", "condition-target"),
        conditions: [{ field: "contains", value: "branch", path: "" }],
      },
      {
        ...graphEdge("to-other", "draft", "other-target"),
        switchCaseOrder: 1,
        conditions: [{ field: "contains", value: "other", path: "" }],
      },
      graphEdge("wait-to-output", "wait-target", "output"),
    );
    await createFlow(page, standaloneHost.origin, requestToken, "Wait and Else", definition);
    const dialog = await openEditor(page, "Wait and Else");
    await canvasNode(dialog, "Draft").click();
    const inspector = dialog.locator("aside").filter({ hasText: "Outgoing" });

    await inspector
      .getByTitle("Drag branch to reorder")
      .last()
      .dragTo(inspector.getByRole("button", { name: "Matched branch", exact: true }));
    await expect(
      dialog
        .locator(".react-flow__edgelabel-renderer")
        .getByText('if contains "other"', { exact: true }),
    ).toBeVisible();
    await expect(inspector.getByText("Wait target waits for this node.")).toBeVisible();
    await inspector.getByRole("button", { name: "Wait target", exact: true }).click();
    const connection = dialog.locator("aside").filter({ hasText: "Label on the canvas" });
    await expect(connection.getByPlaceholder("wait", { exact: true })).toBeVisible();
    await expect(connection.getByRole("button", { name: "Add condition" })).toHaveCount(0);
    await connection.getByRole("button", { name: "Draft", exact: true }).click();
    await inspector.getByRole("button", { name: "Output", exact: true }).click();
    await expect(connection.getByPlaceholder("else", { exact: true })).toBeVisible();
    await expect(connection.getByText("Else runs when no other branch matches.")).toBeVisible();
    await expect(connection.getByRole("button", { name: "Add condition" })).toHaveCount(0);
  },
);
