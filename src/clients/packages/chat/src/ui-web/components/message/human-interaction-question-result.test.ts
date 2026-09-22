import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";

import type { HumanInteractionQuestionResult } from "@agw/chat-core";

const { React, fireEvent, render, screen } = await setupDomEnvironment();
const { HumanInteractionQuestionResultView } =
  await import("./human-interaction-question-result.tsx");

function renderResult(result: HumanInteractionQuestionResult) {
  return render(React.createElement(HumanInteractionQuestionResultView, { result }));
}

const answered: HumanInteractionQuestionResult = {
  cancelled: false,
  items: [
    { question: "Which database provider?", answer: "PostgreSQL" },
    { question: "Run the migration now?", answer: "Yes" },
  ],
};

test("each question renders above its own answer", () => {
  const view = renderResult(answered);
  const paragraphs = [...view.container.querySelectorAll("p")].map((node) => node.textContent);

  assert.deepEqual(paragraphs, [
    "Which database provider?",
    "PostgreSQL",
    "Run the migration now?",
    "Yes",
  ]);
});

test("the summary counts the questions and uses singular wording for one", () => {
  const view = renderResult(answered);
  assert.ok(screen.getByRole("button", { name: /Asked 2 questions/ }));
  view.unmount();

  renderResult({ cancelled: false, items: [answered.items[0]] });
  assert.ok(screen.getByRole("button", { name: /Asked 1 question/ }));
});

test("the answers start expanded and collapse on demand", () => {
  renderResult(answered);
  assert.ok(screen.getByText("PostgreSQL"));

  fireEvent.click(screen.getByRole("button", { name: /Asked 2 questions/ }));

  assert.equal(screen.queryByText("PostgreSQL"), null);
});

test("a cancelled request reports that no answer was given", () => {
  renderResult({
    cancelled: true,
    items: [{ question: "Which database provider?", answer: null }],
  });

  assert.ok(screen.getByText("No answer — request cancelled"));
});
