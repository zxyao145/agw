import assert from "node:assert/strict";
import test from "node:test";

import type { HumanInteractionQuestion } from "../../../services/human-interaction.ts";
import {
  buildQuestionResponse,
  createQuestionSelections,
} from "./human-interaction-questions-state.ts";

const questions: HumanInteractionQuestion[] = [
  {
    question: "Which database?",
    header: "Database",
    multiSelect: false,
    options: [
      {
        label: "PostgreSQL",
        description: "Use the production database.",
        preview: "postgresql://localhost/app",
      },
      { label: "SQLite", description: "Use a local database." },
    ],
  },
  {
    question: "Which checks?",
    header: "Checks",
    multiSelect: true,
    options: [
      { label: "Lint", description: "Run lint checks." },
      { label: "Tests", description: "Run automated tests." },
    ],
  },
];

test("buildQuestionResponse requires an answer for every question", () => {
  assert.equal(buildQuestionResponse(questions, createQuestionSelections(questions)), null);
});

test("buildQuestionResponse preserves option order, custom input, and preview annotation", () => {
  const selections = createQuestionSelections(questions);
  selections["Which database?"] = {
    selected: ["PostgreSQL"],
    otherSelected: false,
    otherText: "",
  };
  selections["Which checks?"] = {
    selected: ["Tests", "Lint"],
    otherSelected: true,
    otherText: "Security scan",
  };

  assert.deepEqual(buildQuestionResponse(questions, selections), {
    answers: {
      "Which database?": "PostgreSQL",
      "Which checks?": "Lint, Tests, Security scan",
    },
    annotations: {
      "Which database?": { preview: "postgresql://localhost/app" },
    },
  });
});
