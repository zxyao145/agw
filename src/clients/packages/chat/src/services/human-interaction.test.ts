import assert from "node:assert/strict";
import test from "node:test";

import {
  getHumanInteractionQuestionResult,
  parseHumanInteractionQuestionResult,
} from "./human-interaction";

test("parseHumanInteractionQuestionResult preserves question order and answers", () => {
  const result = parseHumanInteractionQuestionResult(
    JSON.stringify({
      questions: [
        { question: "Which database?", header: "Database", options: [] },
        { question: "Which runtime?", header: "Runtime", options: [] },
      ],
      answers: {
        "Which runtime?": "Node.js",
        "Which database?": "PostgreSQL",
      },
      cancelled: false,
    }),
  );

  assert.deepEqual(result, {
    cancelled: false,
    items: [
      { question: "Which database?", answer: "PostgreSQL" },
      { question: "Which runtime?", answer: "Node.js" },
    ],
  });
});

test("getHumanInteractionQuestionResult reads a cancelled function result", () => {
  const result = getHumanInteractionQuestionResult([
    {
      messageId: "result-1",
      role: "tool",
      contents: [
        {
          type: "FunctionResultContent",
          content: {
            questions: [{ question: "Continue?" }],
            answers: {},
            cancelled: true,
          },
        },
      ],
    },
  ]);

  assert.deepEqual(result, {
    cancelled: true,
    items: [{ question: "Continue?", answer: null }],
  });
});

test("parseHumanInteractionQuestionResult rejects missing answers and invalid JSON", () => {
  assert.equal(
    parseHumanInteractionQuestionResult({
      questions: [{ question: "Which database?" }],
      answers: {},
    }),
    null,
  );
  assert.equal(parseHumanInteractionQuestionResult("not-json"), null);
});
