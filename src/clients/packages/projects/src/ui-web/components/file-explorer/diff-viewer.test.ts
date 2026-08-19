import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const DIFF_VIEWER_URL = new URL("./diff-viewer.tsx", import.meta.url);
const FILE_VIEWER_URL = new URL("./file-viewer.tsx", import.meta.url);

test("diff viewer starts evenly split and exposes an accessible drag handle", async () => {
  const source = await readFile(DIFF_VIEWER_URL, "utf8");

  assert.match(source, /const DEFAULT_SPLIT_PERCENTAGE = 50/);
  assert.match(source, /gridTemplateColumns: `calc\(\$\{splitPercentage\}% - 2px\) 4px/);
  assert.match(source, /role="separator"/);
  assert.match(source, /aria-orientation="vertical"/);
  assert.match(source, /onPointerDown=\{handlePointerDown\}/);
  assert.match(source, /onPointerMove=\{handlePointerMove\}/);
  assert.match(source, /onKeyDown=\{handleSeparatorKeyDown\}/);
});

test("diff viewer renders GitHub-style line metadata", async () => {
  const [diffViewerSource, fileViewerSource] = await Promise.all([
    readFile(DIFF_VIEWER_URL, "utf8"),
    readFile(FILE_VIEWER_URL, "utf8"),
  ]);

  assert.match(diffViewerSource, /parseUnifiedDiff/);
  assert.match(diffViewerSource, /lines=\{originalLines\}/);
  assert.match(diffViewerSource, /lines=\{modifiedLines\}/);
  assert.match(fileViewerSource, /line\.kind === "addition"/);
  assert.match(fileViewerSource, /line\.kind === "deletion"/);
  assert.match(fileViewerSource, /bg-green-50/);
  assert.match(fileViewerSource, /bg-red-50/);
});

test("diff panes expose synchronized horizontal scrolling", async () => {
  const [diffViewerSource, fileViewerSource] = await Promise.all([
    readFile(DIFF_VIEWER_URL, "utf8"),
    readFile(FILE_VIEWER_URL, "utf8"),
  ]);

  assert.match(diffViewerSource, /new WeakMap<HTMLDivElement, ScrollPosition>/);
  assert.match(diffViewerSource, /source\.scrollHeight - source\.clientHeight/);
  assert.match(diffViewerSource, /target\.scrollHeight - target\.clientHeight/);
  assert.match(diffViewerSource, /source\.scrollWidth - source\.clientWidth/);
  assert.match(diffViewerSource, /target\.scrollWidth - target\.clientWidth/);
  assert.match(diffViewerSource, /target\.scrollLeft = nextPosition\.left/);
  assert.doesNotMatch(diffViewerSource, /requestAnimationFrame/);
  assert.equal((diffViewerSource.match(/onScroll=\{\(event\) => syncScroll/g) ?? []).length, 2);
  assert.equal((diffViewerSource.match(/overflow-auto agw-scrollbar/g) ?? []).length, 2);
  assert.match(fileViewerSource, /isDiffView && "w-full min-w-0"/);
  assert.doesNotMatch(fileViewerSource, /w-max min-w-full/);
  assert.match(fileViewerSource, /isDiffView \? "min-w-max"/);
  assert.match(fileViewerSource, /sticky left-0 z-10.*bg-background/);
  assert.match(fileViewerSource, /export default React\.memo\(FileViewer\)/);
});

test("diff viewer displays non-text diff metadata instead of an empty state", async () => {
  const source = await readFile(DIFF_VIEWER_URL, "utf8");

  assert.match(source, /if \(!diff\.trim\(\)\)/);
  assert.match(source, /if \(!hasRenderableDiffLines\)/);
  assert.match(source, /aria-label="Git diff metadata"/);
  assert.doesNotMatch(source, /!diff\.trim\(\) \|\| originalLines\.length === 0/);
});

test("file viewer renders no-newline annotations", async () => {
  const source = await readFile(FILE_VIEWER_URL, "utf8");

  assert.match(source, /line\.kind === "annotation"/);
  assert.match(source, /<code>\{line\.content\}<\/code>/);
});

test("file comments stay isolated by project-relative path, side, and diff scope", async () => {
  const [diffViewerSource, fileViewerSource] = await Promise.all([
    readFile(DIFF_VIEWER_URL, "utf8"),
    readFile(FILE_VIEWER_URL, "utf8"),
  ]);

  assert.match(fileViewerSource, /comment\.filePath !== filePath/);
  assert.match(fileViewerSource, /comment\.side !== commentSide/);
  assert.match(fileViewerSource, /comment\.diffScope !== diffScope/);
  assert.match(diffViewerSource, /comment\.filePath === filePath/);
  assert.match(diffViewerSource, /comment\.diffScope === scope/);
  assert.match(diffViewerSource, /diffScope=\{scope\}/);
});
