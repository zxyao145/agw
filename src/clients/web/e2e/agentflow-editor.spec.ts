import { expect, type Locator, type Page } from "@playwright/test";
import { randomUUID } from "node:crypto";
import {
  agentflowTest,
  agentNames,
  canvasNode,
  createAgent,
  createFlow,
  flowNameInput,
  graphEdge,
  openEditor,
  publishFlow,
  readFlow,
  sequentialDefinition,
} from "./agentflow-editor-fixture";

function toolbarAction(dialog: Locator, name: string) {
  return dialog.getByRole("button", { name, exact: true });
}

async function selectPanel(dialog: Locator, label: string) {
  await dialog.getByRole("button", { name: label, exact: true }).click();
}

async function selectAgent(page: Page, dialog: Locator, nodeName: string, agentName: string) {
  await canvasNode(dialog, nodeName).click();
  await dialog.getByRole("combobox", { name: "Agent", exact: true }).click();
  await page.getByRole("option").filter({ hasText: agentName }).click();
}

async function openMore(page: Page, dialog: Locator, item: string) {
  await toolbarAction(dialog, "More options").click();
  await page.getByRole("menuitem", { name: item, exact: true }).click();
}

async function save(dialog: Locator, revision: number) {
  const saveButton = toolbarAction(dialog, "Save");
  await expect(saveButton).toBeEnabled();
  await saveButton.click();
  await expect(dialog.getByText(`v${revision}`, { exact: true }).first()).toBeVisible();
  await expect(dialog.getByText("Unsaved changes", { exact: true })).toHaveCount(0);
}

agentflowTest.describe("Agentflow editor", () => {
  agentflowTest.skip(
    ({ viewport }) => !!viewport && viewport.width < 600,
    "Requires the editor's desktop viewport.",
  );

  agentflowTest(
    "AgentflowEditor_DesktopHeader_IsDraggableOutsideControls",
    async ({ page, standaloneHost: _standaloneHost }) => {
      const dialog = await openEditor(page);
      const header = dialog.locator('[data-slot="dialog-header"]');
      const name = flowNameInput(dialog);
      const menu = toolbarAction(dialog, "More options");
      await expect(header).toHaveCSS("-webkit-app-region", "none");

      await page.evaluate(() => {
        document.documentElement.dataset.agwDesktop = "true";
        document.documentElement.dataset.agwPlatform = "darwin";
      });

      await expect(header).toHaveCSS("-webkit-app-region", "drag");
      await expect(name).toHaveCSS("-webkit-app-region", "no-drag");
      await expect(menu).toHaveCSS("-webkit-app-region", "no-drag");
      await name.fill("Renamed Agentflow");
      await expect(name).toHaveValue("Renamed Agentflow");
      await menu.click();
      await expect(page.getByRole("menuitem", { name: "Templates", exact: true })).toBeVisible();
    },
  );

  agentflowTest(
    "AgentflowEditor_SequentialTemplate_ListsProblemsUntilAgentsAreSelected",
    async ({ page, agentIds }) => {
      expect(agentIds).toHaveLength(2);
      const dialog = await openEditor(page);
      await flowNameInput(dialog).fill("Sequential flow");
      await toolbarAction(dialog, "Sequential pipeline").click();
      await dialog.getByTitle("Agentflow problems").click();
      await expect(dialog.getByText(/Agent 1.*agent/i)).toBeVisible();
      await expect(dialog.getByText(/Agent 2.*agent/i)).toBeVisible();
      await selectAgent(page, dialog, "Agent 1", agentNames[0]);
      await selectAgent(page, dialog, "Agent 2", agentNames[1]);
      await expect(
        dialog.getByText("No problems. This Agentflow can be saved and run."),
      ).toBeVisible();
    },
  );

  agentflowTest(
    "AgentflowEditor_BranchWithoutCondition_ShowsProblemThenLabel",
    async ({ page, standaloneHost, requestToken, agentIds }) => {
      await createFlow(
        page,
        standaloneHost.origin,
        requestToken,
        "Branch flow",
        sequentialDefinition(agentIds[0]),
      );
      const dialog = await openEditor(page, "Branch flow");
      await canvasNode(dialog, "Draft").click();
      await toolbarAction(dialog, "Branch").click();
      await dialog.getByTitle("Agentflow problems").click();
      await expect(
        dialog.getByText(/branch from Draft to Output needs a condition/i),
      ).toBeVisible();
      await toolbarAction(dialog, "Add condition").click();
      await dialog.getByLabel("Condition field", { exact: true }).selectOption("contains");
      await dialog.getByLabel("Condition value", { exact: true }).fill("approved");
      await expect(
        dialog
          .locator(".react-flow__edgelabel-renderer")
          .getByText('if contains "approved"', { exact: true }),
      ).toBeVisible();
    },
  );

  agentflowTest(
    "AgentflowEditor_ShowOffscreenEdgeProblem_PansToLabelWithoutRezoom",
    async ({ page, standaloneHost, requestToken, agentIds }) => {
      const definition = sequentialDefinition(agentIds[0]);
      definition.nodes[2].position = { x: 1600, y: 80 };
      const flow = await createFlow(
        page,
        standaloneHost.origin,
        requestToken,
        "Offscreen branch flow",
        definition,
      );
      const dialog = await openEditor(page, flow.name);
      await canvasNode(dialog, "Draft").click();
      await toolbarAction(dialog, "Branch").click();
      await dialog.getByTitle("Agentflow problems").click();
      await expect(
        dialog.getByText(/branch from Draft to Output needs a condition/i),
      ).toBeVisible();

      const edgePath = dialog.locator("path#to-output");
      const labelPoint = await edgePath.evaluate((path) => {
        const element = path as SVGPathElement;
        const point = element.getPointAtLength(element.getTotalLength() / 2);
        return { x: point.x, y: point.y };
      });
      const viewport = dialog.locator(".react-flow__viewport");
      const canvas = dialog.locator("#agentflow-canvas");
      const zoomIn = dialog.getByRole("button", { name: "Zoom in" });
      for (let step = 0; step < 10; step += 1) {
        await zoomIn.click();
      }

      const canvasBounds = await canvas.boundingBox();
      expect(canvasBounds).not.toBeNull();
      const before = await viewport.evaluate((element) => {
        const matrix = new DOMMatrix((element as HTMLElement).style.transform);
        return { x: matrix.e, y: matrix.f, zoom: matrix.a };
      });
      const beforeLabel = {
        x: canvasBounds!.x + before.x + labelPoint.x * before.zoom,
        y: canvasBounds!.y + before.y + labelPoint.y * before.zoom,
      };
      expect(
        beforeLabel.x < canvasBounds!.x + 24 ||
          beforeLabel.x > canvasBounds!.x + canvasBounds!.width - 24 ||
          beforeLabel.y < canvasBounds!.y + 24 ||
          beforeLabel.y > canvasBounds!.y + canvasBounds!.height - 24,
      ).toBe(true);

      await dialog.getByRole("button", { name: "Show on canvas", exact: true }).click();
      await expect(dialog.locator(".react-flow__edge.selected")).toHaveCount(1);
      await expect
        .poll(async () => {
          const after = await viewport.evaluate((element) => {
            const matrix = new DOMMatrix((element as HTMLElement).style.transform);
            return { x: matrix.e, y: matrix.f, zoom: matrix.a };
          });
          return {
            moved: Math.abs(after.x - before.x) > 1 || Math.abs(after.y - before.y) > 1,
            sameZoom: Math.abs(after.zoom - before.zoom) < 0.001,
            centeredX:
              Math.abs(after.x - (canvasBounds!.width / 2 - labelPoint.x * before.zoom)) < 2,
            centeredY:
              Math.abs(after.y - (canvasBounds!.height / 2 - labelPoint.y * before.zoom)) < 2,
          };
        })
        .toMatchObject({
          moved: true,
          sameZoom: true,
          centeredX: true,
          centeredY: true,
        });
    },
  );

  agentflowTest(
    "AgentflowEditor_HumanGateChoices_LabelsEdgesByChoice",
    async ({ page, standaloneHost, requestToken, agentIds }) => {
      const definition = sequentialDefinition(agentIds[0]);
      definition.nodes[1].routingStrategy = "fanOut";
      definition.nodes.push({
        nodeId: "request-changes-output",
        kind: "output",
        name: "Request changes output",
        routingStrategy: "direct",
        position: { x: 850, y: 480 },
        enableSummary: false,
        instructions: null,
        summaryModelProviderId: null,
      });
      definition.edges.push(
        graphEdge("to-request-changes-output", "draft", "request-changes-output"),
      );
      await createFlow(page, standaloneHost.origin, requestToken, "Review flow", definition);
      const dialog = await openEditor(page, "Review flow");
      await dialog
        .getByRole("button", { name: "Human Gate Waits for a person to choose a path." })
        .click();
      const gate = canvasNode(dialog, "Human Gate");
      const inspector = dialog.locator("#agentflow-inspector");
      const choiceLabels = inspector.getByRole("textbox", { name: "Choice label" });
      await inspector.getByLabel("Prompt", { exact: true }).fill("Review the draft.");
      await choiceLabels.first().fill("Approve");
      await inspector.getByRole("button", { name: /Add choice$/ }).click();
      await choiceLabels.last().fill("Request changes");

      const source = gate.locator(".react-flow__handle.source");
      const output = canvasNode(dialog, "Output").locator(".react-flow__handle.target");
      const requestChanges = canvasNode(dialog, "Request changes output").locator(
        ".react-flow__handle.target",
      );
      await canvasNode(dialog, "Draft")
        .locator(".react-flow__handle.source")
        .dragTo(gate.locator(".react-flow__handle.target"));
      await source.dragTo(output);
      await source.dragTo(requestChanges);

      const labels = dialog.locator(".react-flow__edgelabel-renderer");
      await expect(labels.getByText("Approve", { exact: true })).toBeVisible();
      await expect(labels.getByText("Request changes", { exact: true })).toBeVisible();
      await expect(dialog.locator(".react-flow__edge")).toHaveCount(6);
      await save(dialog, 2);
    },
  );

  agentflowTest("AgentflowEditor_SaveAndPublish_ShowsVersions", async ({ page, agentIds }) => {
    expect(agentIds).toHaveLength(2);
    const dialog = await openEditor(page);
    await flowNameInput(dialog).fill("Published flow");
    await toolbarAction(dialog, "Sequential pipeline").click();
    await selectAgent(page, dialog, "Agent 1", agentNames[0]);
    await selectAgent(page, dialog, "Agent 2", agentNames[1]);
    await save(dialog, 1);
    await expect(dialog.getByText("Not published", { exact: true })).toBeVisible();
    await flowNameInput(dialog).fill("Published flow revised");
    await save(dialog, 2);
    await toolbarAction(dialog, "Publish").click();
    await expect(dialog.getByText("Published v2", { exact: true })).toBeVisible();
    await toolbarAction(dialog, "Close editor").click();
    const row = page.getByRole("row").filter({ hasText: "Published flow revised" });
    await expect(row.getByText("v2", { exact: true })).toBeVisible();
  });

  agentflowTest(
    "AgentflowEditor_OpenEarlierVersion_SavesAsNewRevision",
    async ({ page, standaloneHost, requestToken, agentIds }) => {
      const { origin } = standaloneHost;
      const flow = await createFlow(
        page,
        origin,
        requestToken,
        "Versioned flow",
        sequentialDefinition(agentIds[0]),
      );
      const dialog = await openEditor(page, flow.name);
      await canvasNode(dialog, "Draft").click();
      await dialog.getByLabel("Name", { exact: true }).fill("Draft revised");
      await save(dialog, 2);
      await flowNameInput(dialog).fill("Unsaved local name");
      await selectPanel(dialog, "Versions");
      await toolbarAction(dialog, "Open v1").click();
      await expect(canvasNode(dialog, "Draft")).toBeVisible();
      await expect(canvasNode(dialog, "Draft revised")).toHaveCount(0);
      await expect(flowNameInput(dialog)).toHaveValue("Unsaved local name");
      await expect(dialog.getByText("Unsaved changes", { exact: true })).toBeVisible();
      await save(dialog, 3);
      const [first, latest] = await Promise.all([
        readFlow(page, origin, flow.id, 1),
        readFlow(page, origin, flow.id, 3),
      ]);
      expect(latest.definition).toEqual(first.definition);
      expect(latest.agentflow.name).toBe("Unsaved local name");

      await canvasNode(dialog, "Draft").click();
      await toolbarAction(dialog, "Branch").click();
      await dialog.getByTitle("Agentflow problems").click();
      await expect(
        dialog.getByText(/branch from Draft to Output needs a condition/i),
      ).toBeVisible();
      await selectPanel(dialog, "Versions");
      const v1Row = dialog
        .locator("div.flex.items-center.gap-3.text-sm")
        .filter({ has: page.getByRole("button", { name: "Open v1", exact: true }) });
      await v1Row.getByRole("button", { name: "Publish", exact: true }).click();
      await expect(dialog.getByText("Published v1", { exact: true })).toBeVisible();
    },
  );

  agentflowTest(
    "AgentflowEditor_ConcurrentSave_ShowsConflictDialog",
    async ({ page, context, standaloneHost, requestToken, agentIds }) => {
      const flow = await createFlow(
        page,
        standaloneHost.origin,
        requestToken,
        "Shared flow",
        sequentialDefinition(agentIds[0]),
      );
      const second = await context.newPage();
      try {
        const [firstEditor, secondEditor] = await Promise.all([
          openEditor(page, flow.name),
          openEditor(second, flow.name),
        ]);
        await flowNameInput(firstEditor).fill("First draft");
        await flowNameInput(secondEditor).fill("Second draft");
        await save(firstEditor, 2);
        await toolbarAction(secondEditor, "Save").click();
        await expect(second.getByText("This Agentflow was saved somewhere else.")).toBeVisible();
        await expect(second.getByRole("button", { name: "Load v2" })).toBeVisible();
        await expect(second.getByRole("button", { name: "Save as v3" })).toBeVisible();
      } finally {
        await second.close();
      }
    },
  );

  agentflowTest(
    "AgentflowEditor_ExportThenImport_ProducesSameDefinition",
    async ({ page, standaloneHost, requestToken, agentIds }) => {
      const { origin } = standaloneHost;
      const first = await createFlow(
        page,
        origin,
        requestToken,
        "Export source",
        sequentialDefinition(agentIds[0]),
      );
      const dialog = await openEditor(page, first.name);
      await openMore(page, dialog, "Export");
      const exported = await page.getByRole("textbox", { name: "Agentflow JSON" }).inputValue();
      const nextEditor = await openEditor(page);
      await openMore(page, nextEditor, "Import");
      await page.getByRole("textbox", { name: "Agentflow JSON" }).fill(exported);
      await page.getByRole("button", { name: "Import definition", exact: true }).click();
      await flowNameInput(nextEditor).fill("Imported flow");
      const write = page.waitForResponse(
        (response) =>
          response.request().method() === "POST" &&
          new URL(response.url()).pathname === "/api/agentflows",
      );
      await save(nextEditor, 1);
      const { data } = (await (await write).json()) as { data: { id: string } };
      const [original, imported] = await Promise.all([
        readFlow(page, origin, first.id, 1),
        readFlow(page, origin, data.id, 1),
      ]);
      expect(imported.definition).toEqual(original.definition);
    },
  );

  agentflowTest(
    "AgentflowEditor_ImportDuplicateIds_KeepsCanvasAndHistory",
    async ({ page, standaloneHost, requestToken, agentIds }) => {
      const definition = sequentialDefinition(agentIds[0]);
      await createFlow(page, standaloneHost.origin, requestToken, "Import target", definition);
      const dialog = await openEditor(page, "Import target");
      const undo = toolbarAction(dialog, "Undo");
      await expect(undo).toBeDisabled();
      for (const [graph, field, value] of [
        [{ ...definition, nodes: [...definition.nodes, definition.nodes[1]] }, "nodeId", "draft"],
        [
          { ...definition, edges: [...definition.edges, definition.edges[0]] },
          "edgeId",
          "to-draft",
        ],
      ] as const) {
        await openMore(page, dialog, "Import");
        const input = page.getByRole("textbox", { name: "Agentflow JSON" });
        await input.fill(JSON.stringify({ name: "Duplicate", definition: graph }));
        await page.getByRole("button", { name: "Import definition", exact: true }).click();
        await expect(
          page.getByRole("listitem").filter({ hasText: `${field}: ${value}` }),
        ).toBeVisible();
        await expect(input).toBeVisible();
        await page.keyboard.press("Escape");
        await expect(canvasNode(dialog, "Draft")).toBeVisible();
        await expect(undo).toBeDisabled();
      }
    },
  );

  agentflowTest(
    "AgentflowEditor_SelectionAndEscape_UsesControlledChanges",
    async ({ page, standaloneHost, requestToken, agentIds }) => {
      const flow = await createFlow(
        page,
        standaloneHost.origin,
        requestToken,
        "Selection flow",
        sequentialDefinition(agentIds[0]),
      );
      const dialog = await openEditor(page, flow.name);
      const draft = canvasNode(dialog, "Draft");
      const output = canvasNode(dialog, "Output");
      const selectedNodes = dialog.locator(".react-flow__node.selected");

      await draft.click();
      await expect(selectedNodes).toHaveCount(1);
      await expect(dialog.locator("#agentflow-inspector")).toContainText("Draft");

      await output.click({ modifiers: ["ControlOrMeta"] });
      await expect(selectedNodes).toHaveCount(2);
      await draft.click({ modifiers: ["ControlOrMeta"] });
      await expect(selectedNodes).toHaveCount(1);

      await page.keyboard.press("Escape");
      await expect(selectedNodes).toHaveCount(0);
      await canvasNode(dialog, "Input").click();
      await expect(selectedNodes).toHaveCount(1);

      const [draftBounds, outputBounds, canvasBounds] = await Promise.all([
        draft.boundingBox(),
        output.boundingBox(),
        dialog.locator("#agentflow-canvas").boundingBox(),
      ]);
      expect(draftBounds).not.toBeNull();
      expect(outputBounds).not.toBeNull();
      expect(canvasBounds).not.toBeNull();
      const left = Math.max(canvasBounds!.x + 8, Math.min(draftBounds!.x, outputBounds!.x) - 12);
      const top = Math.max(canvasBounds!.y + 8, Math.min(draftBounds!.y, outputBounds!.y) - 12);
      const right = Math.min(
        canvasBounds!.x + canvasBounds!.width - 8,
        Math.max(draftBounds!.x + draftBounds!.width, outputBounds!.x + outputBounds!.width) + 12,
      );
      const bottom = Math.min(
        canvasBounds!.y + canvasBounds!.height - 8,
        Math.max(draftBounds!.y + draftBounds!.height, outputBounds!.y + outputBounds!.height) + 12,
      );
      const selectionStartsOnPane = await page.evaluate(
        ({ x, y }) =>
          document.elementFromPoint(x, y)?.classList.contains("react-flow__pane") ?? false,
        { x: left, y: top },
      );
      expect(selectionStartsOnPane).toBe(true);

      await page.keyboard.down("Shift");
      await page.mouse.move(left, top);
      await page.mouse.down();
      await page.mouse.move(right, bottom, { steps: 8 });
      await expect(dialog.locator(".react-flow__selection")).toBeVisible();
      await page.mouse.up();
      await page.keyboard.up("Shift");
      await expect(selectedNodes).toHaveCount(2);
      await expect(canvasNode(dialog, "Input")).not.toHaveClass(/selected/);

      await toolbarAction(dialog, "More options").click();
      const templatesItem = page.getByRole("menuitem", { name: "Templates", exact: true });
      await expect(templatesItem).toBeVisible();
      await page.keyboard.press("Escape");
      await expect(templatesItem).toHaveCount(0);
      await expect(selectedNodes).toHaveCount(2);

      await page.keyboard.press("Escape");
      await expect(selectedNodes).toHaveCount(0);
      await expect(dialog).toBeVisible();

      await page.keyboard.press("Escape");
      await expect(dialog).toBeVisible();

      await flowNameInput(dialog).focus();
      await page.keyboard.press("Escape");
      await expect(dialog).toBeVisible();
    },
  );

  agentflowTest(
    "AgentflowEditor_CanvasShortcutsWorkFromDialogContent",
    async ({ page, standaloneHost, requestToken, agentIds }) => {
      const flow = await createFlow(
        page,
        standaloneHost.origin,
        requestToken,
        "Canvas keyboard flow",
        sequentialDefinition(agentIds[0]),
      );
      const dialog = await openEditor(page, flow.name);
      const selectedNodes = dialog.locator(".react-flow__node.selected");

      await dialog.focus();
      await page.keyboard.press("ControlOrMeta+a");
      await expect(selectedNodes).toHaveCount(3);
    },
  );

  agentflowTest(
    "AgentflowEditor_PaneClickClearsNodeSelection",
    async ({ page, standaloneHost, requestToken, agentIds }) => {
      const flow = await createFlow(
        page,
        standaloneHost.origin,
        requestToken,
        "Pane click flow",
        sequentialDefinition(agentIds[0]),
      );
      const dialog = await openEditor(page, flow.name);
      const selectedNodes = dialog.locator(".react-flow__node.selected");

      await canvasNode(dialog, "Draft").click();
      await expect(selectedNodes).toHaveCount(1);
      await dialog.locator(".react-flow__pane").click({ position: { x: 16, y: 16 } });
      await expect(selectedNodes).toHaveCount(0);
    },
  );

  agentflowTest(
    "AgentflowEditor_RepeatedKeyboardPasteOffsetsEveryCopy",
    async ({ page, standaloneHost, requestToken, agentIds }) => {
      const flow = await createFlow(
        page,
        standaloneHost.origin,
        requestToken,
        "Paste flow",
        sequentialDefinition(agentIds[0]),
      );
      const dialog = await openEditor(page, flow.name);
      const drafts = canvasNode(dialog, "Draft");

      await drafts.click();
      await page.keyboard.press("ControlOrMeta+c");
      await page.keyboard.press("ControlOrMeta+v");
      await page.keyboard.press("ControlOrMeta+v");
      await expect(drafts).toHaveCount(3);
      const transforms = await drafts.evaluateAll((nodes) =>
        nodes.map((node) => getComputedStyle(node).transform),
      );
      expect(new Set(transforms).size).toBe(3);
    },
  );

  agentflowTest(
    "AgentflowEditor_NodeToolbarAndContextMenuFollowSelection",
    async ({ page, standaloneHost, requestToken, agentIds }) => {
      const flow = await createFlow(
        page,
        standaloneHost.origin,
        requestToken,
        "Canvas menu flow",
        sequentialDefinition(agentIds[0]),
      );
      const dialog = await openEditor(page, flow.name);
      const draft = canvasNode(dialog, "Draft");
      const output = canvasNode(dialog, "Output");
      const duplicateToolbar = dialog.getByRole("button", { name: "Duplicate node" });

      await draft.click();
      await expect(duplicateToolbar).toHaveCount(1);
      await output.click({ modifiers: ["ControlOrMeta"] });
      await expect(duplicateToolbar).toHaveCount(0);

      await draft.click({ button: "right" });
      const menu = page.getByRole("menu");
      await expect(menu.getByRole("menuitem", { name: "Duplicate", exact: true })).toBeVisible();
      await expect(menu.getByRole("menuitem", { name: "Fit view", exact: true })).toHaveCount(0);
      await page.keyboard.press("Escape");

      await dialog.locator('.react-flow__node[data-id="input"]').click({ button: "right" });
      await expect(
        page.getByRole("menu").getByRole("menuitem", { name: "Fit view", exact: true }),
      ).toBeVisible();
      await expect(page.getByRole("menuitem", { name: "Duplicate", exact: true })).toHaveCount(0);
    },
  );

  agentflowTest(
    "AgentflowEditor_ClickSourceHandle_OpensNodePickerBesideHandle",
    async ({ page, standaloneHost, requestToken, agentIds }) => {
      const definition = sequentialDefinition(agentIds[0]);
      const flow = await createFlow(page, standaloneHost.origin, requestToken, "Next step flow", {
        nodes: definition.nodes.slice(0, 2),
        edges: definition.edges.slice(0, 1),
      });
      const dialog = await openEditor(page, flow.name);
      await dialog.evaluate(async (element) => {
        await Promise.all(element.getAnimations().map((animation) => animation.finished));
      });
      const handle = canvasNode(dialog, "Draft").locator(".react-flow__handle.source");
      const anchor = await handle.evaluate((element) => {
        const { x, y, width, height } = element.getBoundingClientRect();
        return { x: x + width / 2, y: y + height / 2 };
      });

      await handle.click();
      const picker = page.locator('[data-slot="popover-content"]').filter({
        has: page.getByRole("textbox", { name: "Search nodes", exact: true }),
      });
      await expect(picker).toBeVisible();
      await expect
        .poll(() =>
          picker.evaluate((element, point) => {
            const { left, right, top, bottom } = element.getBoundingClientRect();
            const horizontal = Math.min(Math.abs(left - point.x), Math.abs(right - point.x));
            const vertical = Math.max(top - point.y, point.y - bottom, 0);
            return Math.max(horizontal, vertical);
          }, anchor),
        )
        .toBeLessThan(24);

      await picker.getByRole("button", { name: /Clear Messages/ }).click();
      await expect(canvasNode(dialog, "Clear Messages")).toBeVisible();
      await expect(dialog.locator(".react-flow__edge")).toHaveCount(2);
    },
  );

  agentflowTest(
    "AgentflowEditor_EdgeContextMenu_InsertsAtEdgeLabel",
    async ({ page, standaloneHost, requestToken, agentIds }) => {
      const definition = sequentialDefinition(agentIds[0]);
      definition.nodes[2].position = { x: 1600, y: 80 };
      definition.edges[1].label = "Insertion marker";
      const flow = await createFlow(
        page,
        standaloneHost.origin,
        requestToken,
        "Edge insertion flow",
        definition,
      );
      const dialog = await openEditor(page, "Edge insertion flow");
      const label = dialog.getByText("Insertion marker", { exact: true });
      await expect(label).toBeVisible();
      const labelPosition = await dialog.locator("path#to-output").evaluate((path) => {
        const pathElement = path as SVGPathElement;
        const middle = pathElement.getPointAtLength(pathElement.getTotalLength() / 2);
        return { x: Math.round(middle.x / 20) * 20, y: Math.round(middle.y / 20) * 20 };
      });
      const [sourceBounds, targetBounds] = await Promise.all([
        canvasNode(dialog, "Draft").boundingBox(),
        canvasNode(dialog, "Output").boundingBox(),
      ]);
      expect(sourceBounds).not.toBeNull();
      expect(targetBounds).not.toBeNull();
      const edgeClickPoint = {
        x:
          sourceBounds!.x +
          sourceBounds!.width +
          (targetBounds!.x - sourceBounds!.x - sourceBounds!.width) * 0.2,
        y: sourceBounds!.y + sourceBounds!.height / 2,
      };
      const hitEdge = await page.evaluate(
        ({ x, y }) =>
          document.elementFromPoint(x, y)?.closest(".react-flow__edge-interaction") !== null,
        edgeClickPoint,
      );
      expect(hitEdge).toBe(true);
      await page.mouse.click(edgeClickPoint.x, edgeClickPoint.y, { button: "right" });
      await page
        .getByRole("menu")
        .getByRole("menuitem", { name: "Insert a node", exact: true })
        .click();
      const nodeSearches = page.getByRole("textbox", { name: "Search nodes" });
      await expect(nodeSearches).toHaveCount(2);
      await nodeSearches.last().fill("Clear Messages");
      await page
        .getByRole("button", { name: "Clear Messages Continues with empty input." })
        .last()
        .click();

      const inserted = canvasNode(dialog, "Clear Messages");
      await expect(inserted).toBeVisible();
      await save(dialog, 2);
      const saved = await readFlow(page, standaloneHost.origin, flow.id, 2);
      const insertedNode = saved.definition.nodes.find((node) => node.name === "Clear Messages") as
        | { nodeId: string; position: { x: number; y: number } }
        | undefined;
      expect(insertedNode).toBeDefined();
      expect(insertedNode!.position.x).toBe(labelPosition.x);
      expect(insertedNode!.position.y).toBe(labelPosition.y);
      expect(saved.definition.edges).toEqual(
        expect.arrayContaining([
          expect.objectContaining({ sourceNodeId: "draft", targetNodeId: insertedNode!.nodeId }),
          expect.objectContaining({ sourceNodeId: insertedNode!.nodeId, targetNodeId: "output" }),
        ]),
      );
    },
  );

  agentflowTest(
    "AgentflowEditor_NodeLibrarySearchMatchesKindAndAddsHumanGate",
    async ({ page, standaloneHost, requestToken, agentIds }) => {
      const scribeName = `scribe-${randomUUID()}`;
      await createAgent(page, standaloneHost.origin, requestToken, scribeName);
      const workflow = await createFlow(
        page,
        standaloneHost.origin,
        requestToken,
        "Reusable workflow",
        sequentialDefinition(agentIds[0]),
      );
      await publishFlow(
        page,
        standaloneHost.origin,
        requestToken,
        workflow.id,
        workflow.latestRevision,
      );
      const dialog = await openEditor(page);
      const library = dialog.locator("#agentflow-node-library");
      const search = library.getByRole("textbox", { name: "Search nodes" });

      await search.fill("Agent");
      await expect(library.getByRole("button", { name: scribeName })).toBeVisible();
      await search.fill("Workflow as Agent");
      await expect(
        library.getByRole("button").filter({ hasText: "Reusable workflow" }),
      ).toBeVisible();
      await search.fill("Human Gate");
      await library.getByRole("button").filter({ hasText: "Human Gate" }).click();
      await expect(canvasNode(dialog, "Human Gate")).toBeVisible();
    },
  );

  agentflowTest(
    "AgentflowEditor_SearchDoesNotRezoomVisibleNodes",
    async ({ page, standaloneHost, requestToken, agentIds }) => {
      const flow = await createFlow(
        page,
        standaloneHost.origin,
        requestToken,
        "Search navigation flow",
        sequentialDefinition(agentIds[0]),
      );
      const dialog = await openEditor(page, flow.name);
      const viewport = dialog.locator(".react-flow__viewport");
      const before = await viewport.getAttribute("style");
      const search = dialog.getByRole("textbox", { name: "Search nodes" });

      await search.fill("Draft");
      await search.press("Enter");
      await page.waitForTimeout(350);

      await expect.poll(() => viewport.getAttribute("style")).toBe(before);
    },
  );
});
