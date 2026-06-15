import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const globalsCss = readFileSync("src/app/globals.css", "utf8");
const mdCardSource = readFileSync("src/components/message/renders/md-card.tsx", "utf8");

function getRuleBody(selector: string): string {
  const match = globalsCss.match(new RegExp(`${selector.replaceAll(".", "\\.")}\\s*\\{([^}]*)\\}`));
  assert.ok(match, `${selector} rule should exist`);
  return match[1];
}

test("markdown unordered lists use outside disc markers", () => {
  const body = getRuleBody(".msg-content-md-ul");

  assert.match(body, /list-disc/);
  assert.match(body, /list-outside/);
  assert.doesNotMatch(body, /list-decimal/);
  assert.doesNotMatch(body, /list-inside/);
});

test("markdown ordered lists use outside decimal markers", () => {
  const body = getRuleBody(".msg-content-md-ol");

  assert.match(body, /list-decimal/);
  assert.match(body, /list-outside/);
  assert.doesNotMatch(body, /list-disc/);
  assert.doesNotMatch(body, /list-inside/);
});

test("markdown list items render paragraph-aware classes", () => {
  assert.match(mdCardSource, /li: \(\{ children \}\) => <li className="msg-content-md-li">/);
  assert.match(mdCardSource, /p: \(\{ children \}\) => <p className="msg-content-md-p">/);
});

test("markdown list item first paragraphs stay on the marker line", () => {
  const body = getRuleBody(".msg-content-md-li > .msg-content-md-p:first-child");

  assert.match(body, /inline/);
  assert.doesNotMatch(body, /block/);
});

test("markdown lists collapse parser whitespace between list items and paragraphs", () => {
  const orderedBody = getRuleBody(".msg-content-md-ol");
  const unorderedBody = getRuleBody(".msg-content-md-ul");
  const listItemBody = getRuleBody(".msg-content-md-li");

  assert.match(orderedBody, /whitespace-normal/);
  assert.match(unorderedBody, /whitespace-normal/);
  assert.match(listItemBody, /whitespace-normal/);
});
