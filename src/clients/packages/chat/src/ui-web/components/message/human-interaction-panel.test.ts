import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";

import type { PendingInteraction } from "@agw/chat-core";

const { React, fireEvent, render, screen } = await setupDomEnvironment();
const { HumanInteractionPanel } = await import("./human-interaction-panel.tsx");

type UserInputInteraction = PendingInteraction & { kind: "user-input" };

function interaction(overrides: Partial<UserInputInteraction>): UserInputInteraction {
  return {
    kind: "user-input",
    interactionId: "interaction-1",
    prompt: "Pick a deployment target.",
    source: {},
    inputKind: "input",
    payload: {},
    ...overrides,
  } as UserInputInteraction;
}

function renderPanel(
  request: UserInputInteraction,
  handlers: { submitted?: unknown[]; cancelled?: number[] } = {},
) {
  return render(
    React.createElement(HumanInteractionPanel, {
      request,
      onSubmit: (response: unknown) => handlers.submitted?.push(response),
      onCancel: () => handlers.cancelled?.push(1),
    }),
  );
}

test("a mode change request renders the mode confirmation", () => {
  renderPanel(interaction({ modeChange: { mode: "plan" } }));

  assert.ok(screen.getByRole("heading", { name: "Change agent mode?" }));
  assert.ok(screen.getByRole("button", { name: "Switch to Plan mode" }));
});

test("a question request renders the question form", () => {
  renderPanel(
    interaction({
      questions: [
        {
          question: "Which database provider?",
          header: "Database",
          multiSelect: false,
          options: [
            { label: "PostgreSQL", description: "Shared deployments" },
            { label: "SQLite", description: "Single host" },
          ],
        },
      ],
    }),
  );

  assert.ok(screen.getByText("Which database provider?"));
  assert.ok(screen.getByText("PostgreSQL"));
});

test("a text request submits the typed value", () => {
  const submitted: unknown[] = [];
  renderPanel(
    interaction({ inputKind: "input", payload: { Placeholder: "Target", Prefill: "staging" } }),
    { submitted },
  );

  const field = screen.getByLabelText("Target") as HTMLTextAreaElement;
  assert.equal(field.value, "staging");
  fireEvent.change(field, { target: { value: "production" } });
  fireEvent.click(screen.getByRole("button", { name: "Submit" }));

  assert.deepEqual(submitted, [{ value: "production" }]);
});

test("a select request submits only a listed option", () => {
  const submitted: unknown[] = [];
  renderPanel(
    interaction({ inputKind: "select", payload: { Options: ["staging", "production"] } }),
    { submitted },
  );

  const submit = screen.getByRole("button", { name: "Submit" });
  assert.equal(submit.hasAttribute("disabled"), true);

  fireEvent.click(screen.getByRole("button", { name: "production" }));
  fireEvent.click(submit);

  assert.deepEqual(submitted, [{ value: "production" }]);
});

test("a confirm request submits a confirmation without a text field", () => {
  const submitted: unknown[] = [];
  renderPanel(interaction({ inputKind: "confirm", payload: { Message: "Deploy now?" } }), {
    submitted,
  });

  assert.ok(screen.getByText("Deploy now?"));
  assert.equal(screen.queryByRole("textbox"), null);
  fireEvent.click(screen.getByRole("button", { name: "Confirm" }));

  assert.deepEqual(submitted, [{ confirmed: true }]);
});

test("an unknown input kind offers only cancellation", () => {
  const cancelled: number[] = [];
  renderPanel(interaction({ inputKind: "voice", payload: {} }), { cancelled });

  assert.ok(screen.getByText("Unsupported interaction"));
  assert.ok(screen.getByText(/cannot render the requested voice interaction/));
  fireEvent.click(screen.getByRole("button", { name: /Cancel request/ }));

  assert.equal(cancelled.length, 1);
});
