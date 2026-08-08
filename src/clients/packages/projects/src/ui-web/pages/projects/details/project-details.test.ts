import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import * as ts from "typescript";

const PROJECT_DETAILS_MODULE_URL = new URL("./project-details.ts", import.meta.url);
const PROJECT_DETAILS_PAGE_URL = new URL("./page.tsx", import.meta.url);
const CONVERSATION_DETAILS_PAGE_URL = new URL("../conversations/details/page.tsx", import.meta.url);
const CONVERSATION_LIST_URL = new URL(
  "../../../components/task/conversation-list.tsx",
  import.meta.url,
);

async function importProjectDetailsModule() {
  try {
    return await import(PROJECT_DETAILS_MODULE_URL.href);
  } catch (error) {
    assert.fail(`project-details module is missing or invalid: ${String(error)}`);
  }
}

function parseSourceFile(sourceText: string) {
  return ts.createSourceFile(
    "page.tsx",
    sourceText,
    ts.ScriptTarget.Latest,
    true,
    ts.ScriptKind.TSX,
  );
}

function walk(node: ts.Node, visit: (node: ts.Node) => void) {
  visit(node);
  node.forEachChild((child) => walk(child, visit));
}

function hasNamedImport(
  sourceFile: ts.SourceFile,
  moduleSpecifier: string,
  importedName: string,
): boolean {
  let matched = false;

  walk(sourceFile, (node) => {
    if (!ts.isImportDeclaration(node)) {
      return;
    }

    if (
      !ts.isStringLiteral(node.moduleSpecifier) ||
      node.moduleSpecifier.text !== moduleSpecifier
    ) {
      return;
    }

    const clause = node.importClause;
    if (!clause?.namedBindings || !ts.isNamedImports(clause.namedBindings)) {
      return;
    }

    matched = clause.namedBindings.elements.some(
      (element) =>
        element.propertyName?.text === importedName || element.name.text === importedName,
    );
  });

  return matched;
}

function hasNonImportIdentifierUsage(sourceFile: ts.SourceFile, identifierName: string): boolean {
  let matched = false;

  walk(sourceFile, (node) => {
    if (!ts.isIdentifier(node) || node.text !== identifierName) {
      return;
    }

    let current: ts.Node | undefined = node.parent;
    while (current) {
      if (ts.isImportSpecifier(current) || ts.isImportClause(current)) {
        return;
      }
      if (
        ts.isJsxExpression(current) ||
        ts.isCallExpression(current) ||
        ts.isVariableDeclaration(current)
      ) {
        matched = true;
        return;
      }
      current = current.parent;
    }
  });

  return matched;
}

function hasJsxTag(sourceFile: ts.SourceFile, tagName: string): boolean {
  let matched = false;

  walk(sourceFile, (node) => {
    if (ts.isJsxSelfClosingElement(node) && node.tagName.getText(sourceFile) === tagName) {
      matched = true;
      return;
    }

    if (ts.isJsxOpeningElement(node) && node.tagName.getText(sourceFile) === tagName) {
      matched = true;
    }
  });

  return matched;
}

function hasDialogTitleIdentifier(sourceFile: ts.SourceFile, identifierName: string): boolean {
  let matched = false;

  walk(sourceFile, (node) => {
    if (
      !ts.isJsxElement(node) ||
      node.openingElement.tagName.getText(sourceFile) !== "DialogTitle"
    ) {
      return;
    }

    const hasIdentifier = node.children.some(
      (child) =>
        ts.isJsxExpression(child) &&
        child.expression !== undefined &&
        ts.isIdentifier(child.expression) &&
        child.expression.text === identifierName,
    );

    if (hasIdentifier) {
      matched = true;
    }
  });

  return matched;
}

function hasCreateTaskButtonWithDisabledProject(sourceFile: ts.SourceFile): boolean {
  let matched = false;

  walk(sourceFile, (node) => {
    if (!ts.isJsxElement(node) || node.openingElement.tagName.getText(sourceFile) !== "Button") {
      return;
    }

    const hasLabel = node.children.some(
      (child) =>
        ts.isJsxExpression(child) &&
        child.expression !== undefined &&
        ts.isIdentifier(child.expression) &&
        child.expression.text === "CREATE_TASK_BUTTON_LABEL",
    );

    const hasDisabledProjectCheck = node.openingElement.attributes.properties.some((attribute) => {
      if (
        !ts.isJsxAttribute(attribute) ||
        !ts.isIdentifier(attribute.name) ||
        attribute.name.text !== "disabled"
      ) {
        return false;
      }

      if (!attribute.initializer || !ts.isJsxExpression(attribute.initializer)) {
        return false;
      }

      const expression = attribute.initializer.expression;
      return (
        expression !== undefined &&
        ts.isPrefixUnaryExpression(expression) &&
        expression.operator === ts.SyntaxKind.ExclamationToken &&
        ts.isIdentifier(expression.operand) &&
        expression.operand.text === "project"
      );
    });

    if (hasLabel && hasDisabledProjectCheck) {
      matched = true;
    }
  });

  return matched;
}

test("createDefaultTaskJobName returns the expected timestamp-and-random shape", async () => {
  const { createDefaultTaskJobName } = await importProjectDetailsModule();
  const now = new Date("2026-04-09T11:22:33.000Z");

  assert.equal(createDefaultTaskJobName(now, 4821), "Job-20260409-4821");
});

test("buildCreateTaskJobRequest maps an agent target to agentType 0", async () => {
  const { QUICK_TASK_TRIGGER_TYPE, buildCreateTaskJobRequest } = await importProjectDetailsModule();
  const now = new Date("2026-04-09T10:20:30.000Z");

  assert.deepEqual(
    buildCreateTaskJobRequest({
      projectId: "11111111-1111-1111-1111-000000000001",
      targetValue: "agent:agent-1",
      jobName: "  Job-20260409-102030-4821  ",
      prompt: "  Summarize recent work  ",
      now,
    }),
    {
      projectId: "11111111-1111-1111-1111-000000000001",
      agentType: 0,
      agentId: "agent-1",
      name: "Job-20260409-102030-4821",
      prompt: "Summarize recent work",
      triggerType: QUICK_TASK_TRIGGER_TYPE,
      triggerValue: "2026-04-09T10:20:40.000Z",
      maxRetryCount: 0,
      isEnabled: true,
    },
  );
});

test("buildCreateTaskJobRequest maps an agentflow target to agentType 1", async () => {
  const { QUICK_TASK_TRIGGER_TYPE, buildCreateTaskJobRequest } = await importProjectDetailsModule();
  const now = new Date("2026-04-09T10:20:30.000Z");

  assert.deepEqual(
    buildCreateTaskJobRequest({
      projectId: "11111111-1111-1111-1111-000000000001",
      targetValue: "agentflow:flow-7",
      jobName: "  Job-20260409-102030-4821  ",
      prompt: "Run the workflow",
      now,
    }),
    {
      projectId: "11111111-1111-1111-1111-000000000001",
      agentType: 1,
      agentId: "flow-7",
      name: "Job-20260409-102030-4821",
      prompt: "Run the workflow",
      triggerType: QUICK_TASK_TRIGGER_TYPE,
      triggerValue: "2026-04-09T10:20:40.000Z",
      maxRetryCount: 0,
      isEnabled: true,
    },
  );
});

test("getProjectDetailItems keeps the existing project detail fields and placeholders", async () => {
  const { getProjectDetailItems } = await importProjectDetailsModule();

  assert.deepEqual(
    getProjectDetailItems({
      description: "  Demo project  ",
      workspace: "",
      extraSetting: null,
    }),
    [
      { label: "Description", value: "Demo project" },
      { label: "Workspace", value: "-", mono: true },
      { label: "Extra Setting", value: "-", mono: true },
    ],
  );
});

test("project page wires details and create-task dialogs through shared helpers", async () => {
  const pageSourceText = await readFile(PROJECT_DETAILS_PAGE_URL, "utf8");
  const pageSource = parseSourceFile(pageSourceText);
  const { CREATE_TASK_BUTTON_LABEL, DETAILS_BUTTON_LABEL, PROJECT_DETAILS_DIALOG_TITLE } =
    await importProjectDetailsModule();

  assert.equal(CREATE_TASK_BUTTON_LABEL, "Create Task");
  assert.equal(DETAILS_BUTTON_LABEL, "Details");
  assert.equal(PROJECT_DETAILS_DIALOG_TITLE, "Project Details");

  assert.ok(
    hasNamedImport(pageSource, "./create-task-dialog", "CreateTaskDialog"),
    "page should import CreateTaskDialog",
  );
  assert.ok(
    hasNamedImport(pageSource, "./project-details", "buildCreateTaskJobRequest"),
    "page should import buildCreateTaskJobRequest",
  );
  assert.ok(
    hasNamedImport(pageSource, "./project-details", "CREATE_TASK_BUTTON_LABEL"),
    "page should import CREATE_TASK_BUTTON_LABEL",
  );
  assert.ok(
    hasNamedImport(pageSource, "./project-details", "PROJECT_DETAILS_DIALOG_TITLE"),
    "page should import PROJECT_DETAILS_DIALOG_TITLE",
  );
  assert.ok(
    hasNonImportIdentifierUsage(pageSource, "buildCreateTaskJobRequest"),
    "page should use buildCreateTaskJobRequest in runtime logic",
  );
  assert.ok(
    hasJsxTag(pageSource, "CreateTaskDialog"),
    "page should render a CreateTaskDialog JSX element",
  );
  assert.ok(
    hasCreateTaskButtonWithDisabledProject(pageSource),
    "Create Task button should use shared label and disabled={!project}",
  );
  assert.ok(
    hasDialogTitleIdentifier(pageSource, "PROJECT_DETAILS_DIALOG_TITLE"),
    "details dialog title should use PROJECT_DETAILS_DIALOG_TITLE",
  );
});

test("project conversation UI uses conversation terminology", async () => {
  const [projectDetailsSource, conversationDetailsSource, conversationListSource] =
    await Promise.all([
      readFile(PROJECT_DETAILS_PAGE_URL, "utf8"),
      readFile(CONVERSATION_DETAILS_PAGE_URL, "utf8"),
      readFile(CONVERSATION_LIST_URL, "utf8"),
    ]);

  assert.match(conversationListSource, />Conversations<\/h2>/);
  assert.match(conversationListSource, /aria-label="Refresh conversations"/);
  assert.match(conversationListSource, />Total conversations:<\/span>/);
  assert.doesNotMatch(
    conversationListSource,
    /Chat Contexts|Refresh chat contexts|Total contexts:/,
  );

  assert.match(projectDetailsSource, /Conversation ID:/);
  assert.match(conversationDetailsSource, /Conversation ID/);
  assert.doesNotMatch(`${projectDetailsSource}\n${conversationDetailsSource}`, /Context ID/);
});
