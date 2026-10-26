import assert from "node:assert/strict";
import test from "node:test";

import { formatWithPrettier, loadPrettierLanguages, prettierSupports } from "./formatting";

test("Prettier formats TypeScript, CSS, Markdown and YAML files, but not Python or C#", async () => {
  const languages = await loadPrettierLanguages();
  for (const fileName of ["a.ts", "a.css", "a.md", "a.yaml"]) {
    assert.equal(prettierSupports(fileName, languages), true, fileName);
  }
  for (const fileName of ["a.py", "a.cs"]) {
    assert.equal(prettierSupports(fileName, languages), false, fileName);
  }
});

test("formatWithPrettier formats TypeScript with the given indentation", async () => {
  const source = "function add(a:number,b:number){\nreturn a+b}\n";
  assert.equal(
    await formatWithPrettier(source, { fileName: "src/app.ts", tabWidth: 4, useTabs: false }),
    "function add(a: number, b: number) {\n    return a + b;\n}\n",
  );
  assert.equal(
    await formatWithPrettier(source, { fileName: "src/app.ts", tabWidth: 2, useTabs: true }),
    "function add(a: number, b: number) {\n\treturn a + b;\n}\n",
  );
});

test("formatWithPrettier rejects code with a syntax error", async () => {
  await assert.rejects(
    formatWithPrettier("const = 1;\n", { fileName: "a.ts", tabWidth: 2, useTabs: false }),
    SyntaxError,
  );
});
