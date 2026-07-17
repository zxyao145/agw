import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import * as ts from "typescript";

const DIALOG_URL = new URL("./create-task-dialog.tsx", import.meta.url);
const PROJECT_DETAILS_URL = new URL("./project-details.ts", import.meta.url);

function parseSourceFile(sourceText: string, fileName: string, scriptKind: ts.ScriptKind) {
  return ts.createSourceFile(fileName, sourceText, ts.ScriptTarget.Latest, true, scriptKind);
}

function walk(node: ts.Node, visit: (child: ts.Node) => void) {
  visit(node);
  node.forEachChild((child) => walk(child, visit));
}

function containsStringLiteral(node: ts.Node, expectedText: string): boolean {
  if (ts.isStringLiteral(node) && node.text === expectedText) {
    return true;
  }

  let matched = false;

  node.forEachChild((child) => {
    if (!matched && containsStringLiteral(child, expectedText)) {
      matched = true;
    }
  });

  return matched;
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

function findJsxElementWithText(
  sourceFile: ts.SourceFile,
  tagName: string,
  expectedText: string,
): boolean {
  let matched = false;

  walk(sourceFile, (node) => {
    if (!ts.isJsxElement(node)) {
      return;
    }

    const openingTagName = node.openingElement.tagName.getText(sourceFile);
    if (openingTagName !== tagName) {
      return;
    }

    const hasExpectedText = node.children.some((child) => {
      if (ts.isJsxText(child)) {
        return child.getText(sourceFile).trim() === expectedText;
      }

      if (ts.isJsxExpression(child) && child.expression) {
        return containsStringLiteral(child.expression, expectedText);
      }

      return false;
    });

    matched ||= hasExpectedText;
  });

  return matched;
}

function hasJsxAttributeStringValue(
  sourceFile: ts.SourceFile,
  tagName: string,
  attributeName: string,
  expectedText: string,
): boolean {
  let matched = false;

  walk(sourceFile, (node) => {
    if (!ts.isJsxSelfClosingElement(node) && !ts.isJsxOpeningElement(node)) {
      return;
    }

    if (node.tagName.getText(sourceFile) !== tagName) {
      return;
    }

    matched = node.attributes.properties.some(
      (attribute) =>
        ts.isJsxAttribute(attribute) &&
        ts.isIdentifier(attribute.name) &&
        attribute.name.text === attributeName &&
        attribute.initializer !== undefined &&
        ts.isStringLiteral(attribute.initializer) &&
        attribute.initializer.text === expectedText,
    );
  });

  return matched;
}

function hasIdentifierUsageInJsx(sourceFile: ts.SourceFile, identifierName: string): boolean {
  let matched = false;

  walk(sourceFile, (node) => {
    if (!ts.isIdentifier(node) || node.text !== identifierName) {
      return;
    }

    let current: ts.Node | undefined = node.parent;
    while (current) {
      if (ts.isJsxExpression(current)) {
        matched = true;
        return;
      }

      if (ts.isImportSpecifier(current) || ts.isImportClause(current)) {
        return;
      }

      current = current.parent;
    }
  });

  return matched;
}

function hasStringLiteralInAncestorJsxTag(
  sourceFile: ts.SourceFile,
  tagName: string,
  expectedText: string,
): boolean {
  let matched = false;

  walk(sourceFile, (node) => {
    if (!ts.isStringLiteral(node) || node.text !== expectedText) {
      return;
    }

    let current: ts.Node | undefined = node.parent;
    while (current) {
      if (
        ts.isJsxElement(current) &&
        current.openingElement.tagName.getText(sourceFile) === tagName
      ) {
        matched = true;
        return;
      }

      current = current.parent;
    }
  });

  return matched;
}

function hasConditionalFallbackText(sourceFile: ts.SourceFile, expectedText: string): boolean {
  let matched = false;

  walk(sourceFile, (node) => {
    const isFallbackExpression =
      ts.isConditionalExpression(node) ||
      (ts.isBinaryExpression(node) &&
        node.operatorToken.kind === ts.SyntaxKind.QuestionQuestionToken);
    if (!isFallbackExpression) {
      return;
    }

    const nodeText = node.getText(sourceFile);
    if (!nodeText.includes(expectedText)) {
      return;
    }

    matched = true;
  });

  return matched;
}

function hasExportedStringConstant(
  sourceFile: ts.SourceFile,
  exportName: string,
  expectedText: string,
): boolean {
  for (const statement of sourceFile.statements) {
    if (!ts.isVariableStatement(statement)) {
      continue;
    }

    const isExported = statement.modifiers?.some(
      (modifier) => modifier.kind === ts.SyntaxKind.ExportKeyword,
    );
    if (!isExported) {
      continue;
    }

    for (const declaration of statement.declarationList.declarations) {
      if (!ts.isIdentifier(declaration.name) || declaration.name.text !== exportName) {
        continue;
      }

      return (
        declaration.initializer !== undefined &&
        ts.isStringLiteral(declaration.initializer) &&
        declaration.initializer.text === expectedText
      );
    }
  }

  return false;
}

test("create-task dialog source includes the required fields and prompt helper", async () => {
  const [dialogSourceText, projectDetailsSourceText] = await Promise.all([
    readFile(DIALOG_URL, "utf8"),
    readFile(PROJECT_DETAILS_URL, "utf8"),
  ]);

  const dialogSource = parseSourceFile(
    dialogSourceText,
    "create-task-dialog.tsx",
    ts.ScriptKind.TSX,
  );
  const projectDetailsSource = parseSourceFile(
    projectDetailsSourceText,
    "project-details.ts",
    ts.ScriptKind.TS,
  );

  assert.ok(
    hasNamedImport(dialogSource, "./project-details", "CREATE_TASK_PROMPT_HELPER_TEXT"),
    "dialog should import CREATE_TASK_PROMPT_HELPER_TEXT from project-details",
  );
  assert.ok(
    hasIdentifierUsageInJsx(dialogSource, "CREATE_TASK_PROMPT_HELPER_TEXT"),
    "dialog should use CREATE_TASK_PROMPT_HELPER_TEXT in JSX",
  );
  assert.ok(
    hasConditionalFallbackText(dialogSource, "Loading project..."),
    "dialog should include a loading-status fallback in the project info block",
  );
  assert.ok(
    findJsxElementWithText(dialogSource, "Label", "Job Name"),
    "dialog should render a Job Name label",
  );
  assert.ok(
    findJsxElementWithText(dialogSource, "Label", "Prompt"),
    "dialog should render a Prompt label",
  );
  assert.ok(
    hasStringLiteralInAncestorJsxTag(dialogSource, "Button", "Create Task"),
    "dialog should render a Create Task button label",
  );
  assert.ok(
    hasJsxAttributeStringValue(
      dialogSource,
      "SelectValue",
      "placeholder",
      "Select agent or agentflow",
    ),
    "dialog should keep the agent/agentflow select placeholder",
  );
  assert.ok(
    hasExportedStringConstant(
      projectDetailsSource,
      "CREATE_TASK_PROMPT_HELPER_TEXT",
      "Prompt is required for task execution.",
    ),
    "project-details should export the prompt helper text constant",
  );
});
