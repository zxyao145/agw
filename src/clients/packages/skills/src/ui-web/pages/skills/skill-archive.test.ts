import assert from "node:assert/strict";
import test from "node:test";
import { deflateRawSync } from "node:zlib";

import { parseSkillMarkdownDescription, readSkillArchiveDescription } from "./skill-archive";

type ZipFixtureFile = { path: string; content: string; deflate?: boolean };

function createZip(files: ZipFixtureFile[], comment = ""): Blob {
  const encoder = new TextEncoder();
  const localParts: Uint8Array<ArrayBuffer>[] = [];
  const centralParts: Uint8Array<ArrayBuffer>[] = [];
  let offset = 0;

  for (const file of files) {
    const name = encoder.encode(file.path);
    const content = encoder.encode(file.content);
    const data = file.deflate ? new Uint8Array(deflateRawSync(content)) : content;
    const method = file.deflate ? 8 : 0;

    const local = new Uint8Array(30 + name.length);
    const localView = new DataView(local.buffer);
    localView.setUint32(0, 0x04034b50, true);
    localView.setUint16(8, method, true);
    localView.setUint32(18, data.length, true);
    localView.setUint32(22, content.length, true);
    localView.setUint16(26, name.length, true);
    local.set(name, 30);

    const central = new Uint8Array(46 + name.length);
    const centralView = new DataView(central.buffer);
    centralView.setUint32(0, 0x02014b50, true);
    centralView.setUint16(10, method, true);
    centralView.setUint32(20, data.length, true);
    centralView.setUint32(24, content.length, true);
    centralView.setUint16(28, name.length, true);
    centralView.setUint32(42, offset, true);
    central.set(name, 46);

    localParts.push(local, data);
    centralParts.push(central);
    offset += local.length + data.length;
  }

  const commentBytes = encoder.encode(comment);
  const end = new Uint8Array(22 + commentBytes.length);
  const endView = new DataView(end.buffer);
  endView.setUint32(0, 0x06054b50, true);
  endView.setUint16(8, files.length, true);
  endView.setUint16(10, files.length, true);
  endView.setUint32(
    12,
    centralParts.reduce((size, part) => size + part.length, 0),
    true,
  );
  endView.setUint32(16, offset, true);
  endView.setUint16(20, commentBytes.length, true);
  end.set(commentBytes, 22);

  return new Blob([...localParts, ...centralParts, end]);
}

function skillMarkdown(description: string): string {
  return `---\nname: sample-skill\ndescription: ${description}\n---\n\n# Sample\n`;
}

test("readSkillArchiveDescription_RootSkillMarkdown_ReturnsDescription", async () => {
  const archive = createZip([{ path: "SKILL.md", content: skillMarkdown("Root skill.") }]);

  assert.equal(await readSkillArchiveDescription(archive), "Root skill.");
});

test("readSkillArchiveDescription_DeflatedSkillInDirectory_IgnoresMacosMetadata", async () => {
  const archive = createZip(
    [
      { path: "__MACOSX/SKILL.md", content: skillMarkdown("Metadata.") },
      { path: "sample-skill/", content: "" },
      { path: "sample-skill/scripts/run.py", content: "print('hi')", deflate: true },
      { path: "sample-skill/SKILL.md", content: skillMarkdown("Nested skill."), deflate: true },
    ],
    "archive comment",
  );

  assert.equal(await readSkillArchiveDescription(archive), "Nested skill.");
});

test("readSkillArchiveDescription_MultipleSkillMarkdownFiles_UsesShallowestFile", async () => {
  const archive = createZip([
    { path: "sample-skill/examples/SKILL.md", content: skillMarkdown("Example skill.") },
    { path: "sample-skill/SKILL.md", content: skillMarkdown("Main skill.") },
  ]);

  assert.equal(await readSkillArchiveDescription(archive), "Main skill.");
});

test("readSkillArchiveDescription_ArchiveWithoutSkillMarkdown_ReturnsNull", async () => {
  const archive = createZip([{ path: "README.md", content: skillMarkdown("Readme.") }]);

  assert.equal(await readSkillArchiveDescription(archive), null);
});

test("readSkillArchiveDescription_InvalidArchive_ReturnsNull", async () => {
  assert.equal(await readSkillArchiveDescription(new Blob(["not a zip archive"])), null);
});

test("parseSkillMarkdownDescription_PlainAndQuotedValues_ReturnsUnquotedText", () => {
  assert.equal(parseSkillMarkdownDescription(skillMarkdown("Plain text.")), "Plain text.");
  assert.equal(
    parseSkillMarkdownDescription(skillMarkdown('"Use \\"quoted\\" text.\\tDone"')),
    'Use "quoted" text.\tDone',
  );
  assert.equal(parseSkillMarkdownDescription(skillMarkdown("'It''s quoted.'")), "It's quoted.");
});

test("parseSkillMarkdownDescription_MultilineValues_FoldsOrKeepsLines", () => {
  assert.equal(
    parseSkillMarkdownDescription(
      "---\nname: sample\ndescription: >-\n  Folded first\n  second line.\n\n  Next paragraph.\nlicense: MIT\n---\n",
    ),
    "Folded first second line.\nNext paragraph.",
  );
  assert.equal(
    parseSkillMarkdownDescription("---\ndescription: |\n  Line one\n    Indented two\n---\n"),
    "Line one\n  Indented two",
  );
  assert.equal(
    parseSkillMarkdownDescription("---\r\ndescription: Plain first\r\n  continued.\r\n---\r\n"),
    "Plain first continued.",
  );
});

test("parseSkillMarkdownDescription_MissingDescriptionOrFrontmatter_ReturnsNull", () => {
  assert.equal(parseSkillMarkdownDescription("---\nname: sample\n---\n# Body\n"), null);
  assert.equal(parseSkillMarkdownDescription("---\ndescription:\n---\n"), null);
  assert.equal(parseSkillMarkdownDescription("# No frontmatter\ndescription: Body text\n"), null);
});
