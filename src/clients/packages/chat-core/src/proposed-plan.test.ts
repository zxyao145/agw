import assert from "node:assert/strict";
import test from "node:test";
import {
  PROPOSED_PLAN_CLOSE_TAG,
  PROPOSED_PLAN_OPEN_TAG,
  parseMessageProposedPlan,
  parseProposedPlan,
} from "./proposed-plan";

test("complete proposed plan tags produce trimmed Markdown", () => {
  assert.deepEqual(
    parseProposedPlan(
      `  \n${PROPOSED_PLAN_OPEN_TAG}\n# Plan\n\n1. Inspect\n${PROPOSED_PLAN_CLOSE_TAG}\n`,
    ),
    {
      leadingMarkdown: "",
      markdown: "# Plan\n\n1. Inspect",
      trailingMarkdown: "",
      isClosed: true,
    },
  );
});

test("an unclosed proposed plan renders the accumulated streaming body", () => {
  assert.deepEqual(parseProposedPlan(`${PROPOSED_PLAN_OPEN_TAG}\n# Plan\n\nStill streaming`), {
    leadingMarkdown: "",
    markdown: "# Plan\n\nStill streaming",
    trailingMarkdown: "",
    isClosed: false,
  });
});

test("partial streaming closing tags are not included in the plan body", () => {
  for (let length = 1; length < PROPOSED_PLAN_CLOSE_TAG.length; length += 1) {
    const result = parseProposedPlan(
      `${PROPOSED_PLAN_OPEN_TAG}\nPlan body\n${PROPOSED_PLAN_CLOSE_TAG.slice(0, length)}`,
    );

    assert.equal(result?.leadingMarkdown, "");
    assert.equal(result?.markdown, "Plan body");
    assert.equal(result?.isClosed, false);
  }
});

test("content after a closed proposed plan remains available as ordinary Markdown", () => {
  assert.deepEqual(
    parseProposedPlan(
      `${PROPOSED_PLAN_OPEN_TAG}\nPlan body\n${PROPOSED_PLAN_CLOSE_TAG}\nUnexpected tail`,
    ),
    {
      leadingMarkdown: "",
      markdown: "Plan body",
      trailingMarkdown: "Unexpected tail",
      isClosed: true,
    },
  );
});

test("leading Markdown is preserved before a root-level proposed plan", () => {
  assert.deepEqual(
    parseProposedPlan(
      `Intro\n\n${PROPOSED_PLAN_OPEN_TAG}\nPlan body\n${PROPOSED_PLAN_CLOSE_TAG}\nTail`,
    ),
    {
      leadingMarkdown: "Intro",
      markdown: "Plan body",
      trailingMarkdown: "Tail",
      isClosed: true,
    },
  );
  assert.deepEqual(parseProposedPlan(`Intro\n${PROPOSED_PLAN_OPEN_TAG}\nStill streaming`), {
    leadingMarkdown: "Intro",
    markdown: "Still streaming",
    trailingMarkdown: "",
    isClosed: false,
  });
});

test("only a standalone root-level proposed plan opening tag is recognized", () => {
  assert.equal(parseProposedPlan("Clarifying question?"), null);
  assert.equal(parseProposedPlan(`Intro ${PROPOSED_PLAN_OPEN_TAG}\nPlan`), null);
  assert.equal(
    parseProposedPlan(["```html", PROPOSED_PLAN_OPEN_TAG, "Plan", "```"].join("\n")),
    null,
  );
  assert.equal(
    parseProposedPlan(["~~~html", PROPOSED_PLAN_OPEN_TAG, "Plan", "~~~"].join("\n")),
    null,
  );
  assert.equal(parseProposedPlan(`    ${PROPOSED_PLAN_OPEN_TAG}\nPlan`), null);
  assert.equal(parseProposedPlan(`\t${PROPOSED_PLAN_OPEN_TAG}\nPlan`), null);
});

test("a proposed plan closing tag inside fenced code does not close the plan", () => {
  const result = parseProposedPlan(
    [
      PROPOSED_PLAN_OPEN_TAG,
      "```text",
      PROPOSED_PLAN_CLOSE_TAG,
      "```",
      "Still in the plan",
      PROPOSED_PLAN_CLOSE_TAG,
    ].join("\n"),
  );

  assert.deepEqual(result, {
    leadingMarkdown: "",
    markdown: ["```text", PROPOSED_PLAN_CLOSE_TAG, "```", "Still in the plan"].join("\n"),
    trailingMarkdown: "",
    isClosed: true,
  });
});

test("only ordinary assistant TextContent is eligible for Plan Card presentation", () => {
  const content = `${PROPOSED_PLAN_OPEN_TAG}\nPlan${PROPOSED_PLAN_CLOSE_TAG}`;

  assert.ok(parseMessageProposedPlan({ role: "assistant" }, "TextContent", content));
  assert.equal(parseMessageProposedPlan({ role: "user" }, "TextContent", content), null);
  assert.equal(
    parseMessageProposedPlan({ role: "assistant" }, "TextReasoningContent", content),
    null,
  );
  assert.equal(
    parseMessageProposedPlan(
      { role: "assistant", additionalProperties: { type: "result" } },
      "TextContent",
      content,
    ),
    null,
  );
});
