import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";

import type { HumanInteractionModeChange as ModeChange, PendingInteraction } from "@agw/chat-core";

const { React, fireEvent, render, screen } = await setupDomEnvironment();
const { HumanInteractionModeChange } = await import("./human-interaction-mode-change.tsx");

function request(mode: ModeChange["mode"]): PendingInteraction & { modeChange: ModeChange } {
  return {
    kind: "user-input",
    interactionId: "interaction-1",
    inputKind: "confirm",
    payload: {},
    prompt: `The agent wants to switch to ${mode} mode.`,
    source: {},
    modeChange: { mode },
  };
}

function renderModeChange(
  mode: ModeChange["mode"],
  handlers: { submitted?: unknown[]; cancelled?: number[] } = {},
) {
  return render(
    React.createElement(HumanInteractionModeChange, {
      request: request(mode),
      onSubmit: (response: unknown) => handlers.submitted?.push(response),
      onCancel: () => handlers.cancelled?.push(1),
    }),
  );
}

test("the confirmation names the mode the agent asked for", () => {
  const view = renderModeChange("plan");
  assert.ok(screen.getByRole("heading", { name: "Change agent mode?" }));
  assert.ok(screen.getByText("The agent wants to switch to plan mode."));
  assert.ok(screen.getByRole("button", { name: "Switch to Plan mode" }));
  view.unmount();

  renderModeChange("execute");
  assert.ok(screen.getByRole("button", { name: "Switch to Execute mode" }));
});

test("the mode changes only after an explicit confirmation", () => {
  const submitted: unknown[] = [];
  renderModeChange("plan", { submitted });

  assert.deepEqual(submitted, []);
  fireEvent.click(screen.getByRole("button", { name: "Switch to Plan mode" }));

  assert.deepEqual(submitted, [{ confirmed: true }]);
});

test("cancelling reports the refusal without a response", () => {
  const submitted: unknown[] = [];
  const cancelled: number[] = [];
  renderModeChange("plan", { submitted, cancelled });

  fireEvent.click(screen.getByRole("button", { name: /Cancel/ }));

  assert.deepEqual(submitted, []);
  assert.equal(cancelled.length, 1);
});
