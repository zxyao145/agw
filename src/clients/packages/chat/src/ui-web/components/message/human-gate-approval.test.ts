import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";

import type { ApprovalScope, PendingInteraction, PermissionMode } from "@agw/chat-core";

const { React, fireEvent, render, screen } = await setupDomEnvironment();
const { HumanGateApproval } = await import("./human-gate-approval.tsx");

type Approval = { scope: ApprovalScope; responseText?: string };

const toolApproval: PendingInteraction = {
  kind: "tool-approval",
  interactionId: "interaction-1",
  prompt: "Run the migration script?",
  source: { toolName: "command_execution" },
  arguments: { command: "dotnet ef database update" },
};

function workflowGate(mode: string): PendingInteraction {
  return {
    kind: "workflow-gate",
    interactionId: "interaction-2",
    prompt: "Confirm the deployment target.",
    source: { nodeName: "Deploy gate" },
    mode,
    inputPreview: "target = staging",
  };
}

function renderApproval(
  request: PendingInteraction,
  permissionMode?: PermissionMode,
  approvals: Approval[] = [],
  rejections: (string | undefined)[] = [],
) {
  return render(
    React.createElement(HumanGateApproval, {
      request: request as Parameters<typeof HumanGateApproval>[0]["request"],
      permissionMode,
      onApprove: (scope: ApprovalScope, responseText?: string) =>
        approvals.push({ scope, responseText }),
      onReject: (responseText?: string) => rejections.push(responseText),
    }),
  );
}

test("a tool approval shows the tool, the prompt, and its arguments", () => {
  renderApproval(toolApproval);

  assert.ok(screen.getByText("command_execution"));
  assert.ok(screen.getByText("Run the migration script?"));
  assert.ok(screen.getByText(/dotnet ef database update/));
  assert.ok(screen.getByText("tool-approval"));
});

test("full access answers tool approvals without asking", () => {
  const view = renderApproval(toolApproval, "fullAccess");

  assert.equal(view.container.innerHTML, "");
});

test("always ask offers a single one-time approval", () => {
  const approvals: Approval[] = [];
  renderApproval(toolApproval, "alwaysAsk", approvals);

  assert.equal(screen.queryByRole("button", { name: /Always allow tool/ }), null);
  assert.equal(screen.queryByRole("button", { name: /Allow same arguments/ }), null);
  fireEvent.click(screen.getByRole("button", { name: /Allow once/ }));

  assert.deepEqual(approvals, [{ scope: "Once", responseText: undefined }]);
});

test("allow-same-arguments approves by argument list", () => {
  const approvals: Approval[] = [];
  renderApproval(toolApproval, "allowSameArguments", approvals);

  assert.equal(screen.queryByRole("button", { name: /Allow once/ }), null);
  fireEvent.click(screen.getByRole("button", { name: /Allow same arguments/ }));

  assert.deepEqual(approvals, [{ scope: "AlwaysArguments", responseText: undefined }]);
});

test("an unset permission mode offers every approval scope", () => {
  const approvals: Approval[] = [];
  renderApproval(toolApproval, undefined, approvals);

  fireEvent.click(screen.getByRole("button", { name: /Allow once/ }));
  fireEvent.click(screen.getByRole("button", { name: /Allow same arguments/ }));
  fireEvent.click(screen.getByRole("button", { name: /Always allow tool/ }));

  assert.deepEqual(
    approvals.map((approval) => approval.scope),
    ["Once", "AlwaysArguments", "AlwaysTool"],
  );
});

test("rejecting a tool approval reports no response text", () => {
  const rejections: (string | undefined)[] = [];
  renderApproval(toolApproval, "alwaysAsk", [], rejections);

  fireEvent.click(screen.getByRole("button", { name: /Reject/ }));

  assert.deepEqual(rejections, [undefined]);
});

test("a workflow gate shows its node, mode, and input preview", () => {
  renderApproval(workflowGate("Approval"));

  assert.ok(screen.getByText("Deploy gate"));
  assert.ok(screen.getByText("approval"));
  assert.ok(screen.getByText("target = staging"));
  assert.ok(screen.getByRole("button", { name: /Approve/ }));
  assert.ok(screen.getByRole("button", { name: /Reject/ }));
  assert.equal(screen.queryByPlaceholderText("Response"), null);
});

test("an input gate collects text and submits it with the approval", () => {
  const approvals: Approval[] = [];
  renderApproval(workflowGate("Input"), undefined, approvals);

  const response = screen.getByPlaceholderText("Response");
  fireEvent.change(response, { target: { value: "  deploy to staging  " } });
  fireEvent.click(screen.getByRole("button", { name: /Submit/ }));

  assert.deepEqual(approvals, [{ scope: "Once", responseText: "deploy to staging" }]);
});

test("an input gate interrupts with the text typed so far", () => {
  const rejections: (string | undefined)[] = [];
  renderApproval(workflowGate("Input"), undefined, [], rejections);

  fireEvent.change(screen.getByPlaceholderText("Response"), { target: { value: "stop" } });
  fireEvent.click(screen.getByRole("button", { name: /Interrupt/ }));

  assert.deepEqual(rejections, ["stop"]);
});

test("a new interaction starts with an empty response", () => {
  const view = renderApproval(workflowGate("Input"));
  fireEvent.change(screen.getByPlaceholderText("Response"), { target: { value: "first" } });

  view.rerender(
    React.createElement(HumanGateApproval, {
      request: {
        ...workflowGate("Input"),
        interactionId: "interaction-3",
      } as Parameters<typeof HumanGateApproval>[0]["request"],
      onApprove: () => {},
      onReject: () => {},
    }),
  );

  assert.equal((screen.getByPlaceholderText("Response") as HTMLTextAreaElement).value, "");
});
