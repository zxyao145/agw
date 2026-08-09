import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import * as React from "react";
import { renderToStaticMarkup } from "react-dom/server";
import PlanCard from "./plan-card";

const planCardSource = readFileSync(new URL("./plan-card.tsx", import.meta.url), "utf8");
const rendererSource = readFileSync(new URL("./index.tsx", import.meta.url), "utf8");
const messageSource = readFileSync(new URL("../message.tsx", import.meta.url), "utf8");
const planCardHtml = renderToStaticMarkup(
  React.createElement(PlanCard, {
    markdown: "# Decision-complete plan",
    trailingMarkdown: "",
    isClosed: true,
  }),
);

test("Plan Card renders parsed Markdown without protocol tags", () => {
  assert.match(planCardHtml, /<section/);
  assert.match(planCardHtml, /<h1>Decision-complete plan<\/h1>/);
  assert.match(planCardHtml, /aria-label="Copy plan"/);
  assert.doesNotMatch(planCardHtml, /proposed_plan/);
});

test("Plan Card uses a full-width accessible chat surface", () => {
  const sectionClassName = planCardHtml.match(/<section[^>]*class="([^"]*)"/)?.[1] ?? "";

  assert.match(planCardHtml, /<section[^>]*aria-labelledby=/);
  assert.match(planCardHtml, /<svg[^>]*aria-hidden="true"/);
  assert.match(planCardHtml, /<h2[^>]*>Plan<\/h2>/);
  for (const className of ["w-full", "overflow-hidden", "rounded-2xl", "border", "bg-card"]) {
    assert.ok(sectionClassName.split(" ").includes(className));
  }
  assert.match(messageSource, /const isProposedPlan/);
  assert.match(messageSource, /isProposedPlan[\s\S]*?\? "w-full"/);
  assert.match(rendererSource, /node\.proposedPlan/);
  assert.match(rendererSource, /<PlanCard \{\.\.\.node\.proposedPlan\} \/>/);
});

test("Plan Card copies only its parsed Markdown with visible accessible feedback", () => {
  assert.match(planCardSource, /navigator\.clipboard\.writeText\(normalizedMarkdown\)/);
  assert.match(planCardSource, /aria-label=\{copied \? "Plan copied" : "Copy plan"\}/);
  assert.match(planCardSource, /<Copy/);
  assert.match(planCardSource, /<Check/);
  assert.match(planCardSource, /disabled=\{!normalizedMarkdown\}/);
  assert.doesNotMatch(planCardSource, /Download|ThumbsUp|ThumbsDown|Maximize/);
});

test("only ordinary assistant text is eligible for Plan Card parsing", () => {
  assert.match(messageSource, /parseMessageProposedPlan\(message, type, content\)/);
});
