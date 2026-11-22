import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";
import type { PendingInteraction } from "@agw/chat-core";

const { React, fireEvent, render, screen } = await setupDomEnvironment();
const { WorkflowGateCard } = await import("./workflow-gate-card.tsx");

const request: Extract<PendingInteraction, { kind: "workflow-gate" }> = {
  kind: "workflow-gate",
  interactionId: "review-1",
  prompt: "Review the draft.",
  source: { nodeName: "Review" },
  choices: [
    { choiceId: "approve", label: "Approve" },
    { choiceId: "changes", label: "Request changes" },
  ],
  allowResponseText: true,
  inputPreview: "Draft",
};

test("WorkflowGateCard sends the chosen choiceId and the reply text", () => {
  const responses: unknown[] = [];
  render(
    React.createElement(WorkflowGateCard, {
      request,
      onRespond: (response) => responses.push(response),
    }),
  );
  assert.ok(screen.getByText("Review"));
  assert.ok(screen.getByText("Review the draft."));
  assert.ok(screen.getByText("Draft"));
  fireEvent.change(screen.getByRole("textbox", { name: "Response" }), {
    target: { value: "  Shorten the intro.  " },
  });
  fireEvent.click(screen.getByRole("button", { name: "Request changes" }));
  assert.deepEqual(responses, [
    {
      kind: "workflow-gate",
      interactionId: "review-1",
      approved: true,
      choiceId: "changes",
      responseText: "Shorten the intro.",
    },
  ]);
});

test("Stop workflow sends a declined response and the reply field follows allowResponseText", () => {
  const responses: unknown[] = [];
  render(
    React.createElement(WorkflowGateCard, {
      request: { ...request, allowResponseText: false },
      onRespond: (response) => responses.push(response),
    }),
  );
  assert.equal(screen.queryByRole("textbox"), null);
  fireEvent.click(screen.getByRole("button", { name: "Stop workflow" }));
  assert.deepEqual(responses, [
    { kind: "workflow-gate", interactionId: "review-1", approved: false },
  ]);
});
