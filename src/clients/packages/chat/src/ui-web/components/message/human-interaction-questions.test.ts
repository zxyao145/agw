import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";

const { React, fireEvent, render, screen } = await setupDomEnvironment();
const { HumanInteractionQuestions } = await import("./human-interaction-questions.tsx");
const { HumanInteractionPanel } = await import("./human-interaction-panel.tsx");

for (const inputKind of ["confirm", "select", "input", "editor"]) {
  test(`Pi ${inputKind} submits provider data and retains an explicit cancellation action`, () => {
    const submitted: unknown[] = [];
    let cancelled = false;
    render(
      React.createElement(HumanInteractionPanel, {
        request: {
          kind: "user-input",
          interactionId: `pi-${inputKind}`,
          source: {},
          prompt: "Pi input",
          inputKind,
          payload: { Options: ["A", "B"], Placeholder: "Answer", Prefill: "Original" },
        },
        onSubmit: (value) => {
          submitted.push(value);
        },
        onCancel: () => {
          cancelled = true;
        },
      }),
    );
    if (inputKind === "select") fireEvent.click(screen.getByRole("button", { name: "B" }));
    else if (inputKind !== "confirm")
      fireEvent.change(screen.getByRole("textbox"), { target: { value: "  revised\ntext  " } });
    fireEvent.click(
      screen.getByRole("button", { name: inputKind === "confirm" ? "Confirm" : "Submit" }),
    );
    assert.deepEqual(submitted, [
      inputKind === "confirm"
        ? { confirmed: true }
        : { value: inputKind === "select" ? "B" : "  revised\ntext  " },
    ]);
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    assert.equal(cancelled, true);
  });
}

const request = {
  kind: "user-input" as const,
  interactionId: "request-1",
  source: {},
  inputKind: "questions",
  payload: {},
  prompt: "Choose one.",
  questions: [
    {
      question: "Which approach?",
      header: "Approach",
      multiSelect: false,
      options: [
        {
          label: "Preview option",
          description: "This option has a preview.",
          preview: "## Preview content\n\nThe preview remains inside its panel.",
        },
        {
          label: "No preview option",
          description: "This option does not have a preview.",
        },
      ],
    },
  ],
};

function renderQuestions(requestToRender = request) {
  return render(
    React.createElement(HumanInteractionQuestions, {
      request: requestToRender,
      onSubmit: () => {},
      onCancel: () => {},
    }),
  );
}

test("hovering an option without a preview keeps the preview region mounted", () => {
  renderQuestions();

  const previewRegion = screen.getByRole("region", { name: "Option preview" });
  const noPreviewOption = screen.getByText("No preview option").closest("label");
  assert.ok(noPreviewOption);
  assert.ok(screen.getByText("Hover or focus an option to preview it."));

  fireEvent.mouseEnter(noPreviewOption);

  assert.ok(screen.getByText("No preview is available for this option."));
  assert.strictEqual(screen.getByRole("region", { name: "Option preview" }), previewRegion);
});

test("questions without previews do not reserve a preview region", () => {
  renderQuestions({
    ...request,
    questions: [
      {
        ...request.questions[0]!,
        options: request.questions[0]!.options.map(({ label, description }) => ({
          label,
          description,
        })),
      },
    ],
  });

  assert.equal(screen.queryByRole("region", { name: "Option preview" }), null);
});

test("focusing an option with a preview updates content without replacing the region", () => {
  renderQuestions();

  const previewRegion = screen.getByRole("region", { name: "Option preview" });
  const previewOption = screen.getByRole("radio", { name: /^Preview option/ });

  fireEvent.focus(previewOption);

  assert.ok(screen.getByText("Preview content"));
  assert.strictEqual(screen.getByRole("region", { name: "Option preview" }), previewRegion);
});
