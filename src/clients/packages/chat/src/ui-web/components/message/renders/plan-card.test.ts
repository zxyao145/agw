import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";

const { React, act, fireEvent, render, screen, waitFor, window } = await setupDomEnvironment();
const { default: PlanCard } = await import("./plan-card.tsx");

function renderPlan(overrides: Partial<Parameters<typeof PlanCard>[0]> = {}) {
  return render(
    React.createElement(PlanCard, {
      leadingMarkdown: "",
      markdown: "# Decision-complete plan",
      trailingMarkdown: "",
      isClosed: true,
      ...overrides,
    }),
  );
}

test("the plan renders as parsed Markdown under a labelled section", () => {
  const view = renderPlan();
  const section = view.container.querySelector("section");

  assert.ok(section);
  assert.ok(screen.getByRole("heading", { name: "Plan", level: 2 }));
  assert.ok(screen.getByRole("heading", { name: "Decision-complete plan", level: 1 }));
  assert.equal(
    section.getAttribute("aria-labelledby"),
    screen.getByRole("heading", { name: "Plan", level: 2 }).id,
  );
  assert.doesNotMatch(view.container.innerHTML, /proposed_plan/);
});

test("surrounding text stays outside the plan section in reading order", () => {
  const view = renderPlan({
    leadingMarkdown: "Context before the plan",
    trailingMarkdown: "Notes after the plan",
  });
  const html = view.container.innerHTML;

  assert.ok(html.indexOf("Context before the plan") < html.indexOf("<section"));
  assert.ok(html.indexOf("Notes after the plan") > html.indexOf("</section>"));
});

test("copying the plan puts only its Markdown on the clipboard", async () => {
  renderPlan({ leadingMarkdown: "Context", markdown: "  # Plan body  " });

  await act(async () => {
    fireEvent.click(screen.getByRole("button", { name: "Copy plan" }));
  });

  assert.equal(await window.navigator.clipboard.readText(), "# Plan body");
  await waitFor(() => assert.ok(screen.getByRole("button", { name: "Plan copied" })));
});

test("an empty plan cannot be copied", () => {
  renderPlan({ markdown: "   " });

  assert.equal(screen.getByRole("button", { name: "Copy plan" }).hasAttribute("disabled"), true);
});

test("the plan card offers no action beyond copying", () => {
  renderPlan();

  assert.deepEqual(
    screen.getAllByRole("button").map((button) => button.getAttribute("aria-label")),
    ["Copy plan"],
  );
});
