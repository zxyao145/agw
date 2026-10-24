import assert from "node:assert/strict";
import { mkdir, realpath, symlink, writeFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import test from "node:test";

import { createScratchDirectory } from "../scratch-directory.test-helper";
import { compileCssTheme } from "./extension-theme-css";
import {
  MAX_THEME_ASSET_BYTES,
  MAX_THEME_CSS_FILE_BYTES,
  MAX_THEME_FILES,
  MAX_THEME_IMPORT_DEPTH,
  MAX_THEME_IMPORT_EXPANSIONS,
  MAX_THEME_INPUT_BYTES,
  ThemeCompileError,
  verifyThemeCssText,
} from "./extension-theme-resources";

const PNG = Buffer.from(
  "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==",
  "base64",
);
const WOFF2 = Buffer.from("wOF2-test-font");

async function writeFiles(root: string, files: Record<string, string | Uint8Array>): Promise<void> {
  for (const [path, content] of Object.entries(files)) {
    const file = join(root, ...path.split("/"));
    await mkdir(dirname(file), { recursive: true });
    await writeFile(file, content);
  }
}

/** 在临时目录中写入主题文件并编译入口。Writes theme files into a scratch directory and compiles the entry. */
async function compileFiles(files: Record<string, string | Uint8Array>, entry = "theme.css") {
  const scratch = await createScratchDirectory("theme-css");
  try {
    await writeFiles(scratch.path, files);
    return await compileCssTheme(await realpath(scratch.path), entry);
  } finally {
    await scratch.dispose();
  }
}

async function rejectsWith(
  files: Record<string, string | Uint8Array>,
  code: string,
  ...fragments: string[]
): Promise<void> {
  await assert.rejects(compileFiles(files), (error: unknown) => {
    assert.ok(error instanceof ThemeCompileError, String(error));
    assert.equal(error.code, code, error.message);
    for (const fragment of fragments) assert.ok(error.message.includes(fragment), error.message);
    return true;
  });
}

function dataUrlBody(cssText: string, mimeType: string): Buffer {
  const match = new RegExp(
    `data:${mimeType.replace("+", "\\+")};base64,([A-Za-z0-9+/=]+)`,
    "u",
  ).exec(cssText);
  assert.ok(match, `no ${mimeType} data URL in ${cssText.slice(0, 200)}`);
  return Buffer.from(match[1], "base64");
}

/* ---------- 样式内容 Style content ---------- */

test("standard CSS keeps variables, component rules, layout, modes, conditions, layers and animations", async () => {
  const css = `@layer base, theme;
:root { --custom-gap: 20px; }
:root:not(.dark) { --background: #fffdf8; }
:root.dark { --background: #24211d; }
:root[data-agw-document="desktop"] .agw-main-workspace { display: grid; grid-template-columns: 1fr 2fr; gap: var(--custom-gap); }
.toolbar { display: flex; align-items: center; }
@media (max-width: 800px) { .toolbar { flex-direction: column; } }
@supports (display: grid) { .panel { display: grid; } }
@layer theme { .card { border-radius: 12px; } }
@keyframes pulse { from { opacity: 1; } to { opacity: 0.5; } }
.card { & .title { color: var(--foreground); } }
`;
  const result = await compileFiles({ "theme.css": css });
  assert.equal(result.cssText, css);
  assert.equal(result.byteLength, Buffer.byteLength(css));
  assert.match(result.styleId, /^[0-9a-f]{64}$/u);
});

test("the styleId is the SHA-256 of the processed text", async () => {
  const first = await compileFiles({ "theme.css": ":root { --a: 1px; }" });
  const second = await compileFiles({ "theme.css": ":root { --a: 1px; }" });
  const third = await compileFiles({ "theme.css": ":root { --a: 2px; }" });
  assert.equal(first.styleId, second.styleId);
  assert.notEqual(first.styleId, third.styleId);
});

/* ---------- 导入 Imports ---------- */

test("nested and repeated imports expand in source order with their conditions", async () => {
  const result = await compileFiles({
    "theme.css": [
      '@import "parts/a.css";',
      '@import "parts/a.css" screen;',
      "@import url(parts/b.css) supports(display: grid);",
      '@import "/parts/c.css" layer(named);',
      '@import "parts/c.css" layer;',
      ".entry { color: red; }",
    ].join("\n"),
    "parts/a.css": '@import "nested/d.css";\n.a { color: blue; }',
    "parts/nested/d.css": ".d { color: green; }",
    "parts/b.css": ".b { color: black; }",
    "parts/c.css": ".c { color: white; }",
  });
  const order = [...result.cssText.matchAll(/\.(a|b|c|d|entry) \{/gu)].map((match) => match[1]);
  assert.deepEqual(order, ["d", "a", "d", "a", "b", "c", "c", "entry"]);
  assert.match(result.cssText, /@media screen \{\s*\.d/u);
  assert.match(result.cssText, /@supports \(display: grid\) \{\s*\.b/u);
  assert.match(result.cssText, /@layer named \{\s*\.c/u);
  assert.match(result.cssText, /@layer \{\s*\.c/u);
  assert.equal(result.cssText.includes("@import"), false);
  assert.equal(result.dependencies.length, 5);
});

test("circular imports are validation errors", async () => {
  await rejectsWith(
    {
      "theme.css": '@import "a.css";',
      "a.css": '@import "b.css";',
      "b.css": '@import "a.css";',
    },
    "THEME_IMPORT_INVALID",
    "Circular import: theme.css → a.css → b.css → a.css",
    "b.css:1:1",
  );
});

test("imports must be package CSS files at the top of a stylesheet", async () => {
  await rejectsWith(
    { "theme.css": '@import "https://example.com/a.css";' },
    "THEME_RESOURCE_INVALID",
    "refers outside",
  );
  await rejectsWith({ "theme.css": '@import "//example.com/a.css";' }, "THEME_RESOURCE_INVALID");
  await rejectsWith(
    { "theme.css": '@import "a.css?v=1";', "a.css": "" },
    "THEME_RESOURCE_INVALID",
    "query",
  );
  await rejectsWith(
    { "theme.css": '@import "a.png";', "a.png": PNG },
    "THEME_IMPORT_INVALID",
    "not a package .css file",
  );
  await rejectsWith(
    { "theme.css": '@import "data:text/css;base64,LmF7fQ==";' },
    "THEME_IMPORT_INVALID",
    "data URLs",
  );
  await rejectsWith(
    { "theme.css": '.a { color: red; }\n@import "b.css";', "b.css": "" },
    "THEME_IMPORT_INVALID",
    "must precede",
    "theme.css:2:1",
  );
  await rejectsWith(
    { "theme.css": '@media screen { @import "b.css"; }', "b.css": "" },
    "THEME_IMPORT_INVALID",
    "top of a stylesheet",
  );
  // postcss 不能解析带 escape 的 at-rule 名称。postcss cannot parse escaped at-rule names.
  await rejectsWith({ "theme.css": '@\\69 mport "b.css";', "b.css": "" }, "THEME_PARSE_FAILED");
  await rejectsWith(
    { "theme.css": '@import "b\\2e css";', "b.css": "" },
    "THEME_IMPORT_INVALID",
    "escapes",
  );
  await rejectsWith(
    { "theme.css": '@import "missing.css";' },
    "THEME_FILE_NOT_FOUND",
    '"missing.css" does not exist',
    "theme.css:1:1",
  );
  await rejectsWith({ "theme.css": '@import "../outside.css";' }, "PACKAGE_PATH_OUTSIDE_ROOT");
});

test("layer statements keep their order around imports", async () => {
  const result = await compileFiles({
    "theme.css": '@layer base, theme;\n@import "a.css";\n.entry { color: red; }',
    "a.css": "@layer base { .a { color: blue; } }",
  });
  assert.ok(result.cssText.indexOf("@layer base, theme") < result.cssText.indexOf("@layer base {"));
  await rejectsWith(
    { "theme.css": '@import "a.css";\n@layer x;\n@import "b.css";', "a.css": "", "b.css": "" },
    "THEME_IMPORT_INVALID",
    "@layer statements",
  );
  await rejectsWith(
    { "theme.css": '@import "a.css";', "a.css": "@layer x;\n.a { color: red; }" },
    "THEME_IMPORT_INVALID",
    "Imported files cannot start with @layer statements",
    "a.css:1:1",
  );
});

test("relative resources resolve against the file that refers to them", async () => {
  const result = await compileFiles({
    "theme.css": '@import "styles/parts/card.css";\n.root { background: url("images/root.png"); }',
    "styles/parts/card.css":
      '.card { background-image: url(../../images/card.png); mask: url("/images/mask.svg#shape"); }',
    "images/root.png": PNG,
    "images/card.png": PNG,
    "images/mask.svg":
      '<svg xmlns="http://www.w3.org/2000/svg"><path id="shape" d="M0 0h1v1z"/></svg>',
  });
  assert.equal(result.cssText.includes("images/"), false);
  assert.match(result.cssText, /url\(data:image\/svg\+xml;base64,[A-Za-z0-9+/=]+#shape\)/u);
  assert.deepEqual(dataUrlBody(result.cssText, "image/png"), PNG);
  assert.equal(result.dependencies.length, 5);
});

test("fonts inline with font types and fragments and data URLs stay unchanged", async () => {
  const dataPng = `data:image/png;base64,${PNG.toString("base64")}`;
  const result = await compileFiles({
    "theme.css": `@font-face { font-family: Custom; src: url(fonts/custom.woff2) format("woff2"), local(Arial); }
.a { filter: url(#blur); background: url("${dataPng}"); }`,
    "fonts/custom.woff2": WOFF2,
  });
  assert.deepEqual(dataUrlBody(result.cssText, "font/woff2"), WOFF2);
  assert.ok(result.cssText.includes("url(#blur)"));
  assert.ok(result.cssText.includes(dataPng));
});

test("every allowed image and font type inlines with its MIME type", async () => {
  const images = {
    png: "image/png",
    jpg: "image/jpeg",
    jpeg: "image/jpeg",
    gif: "image/gif",
    webp: "image/webp",
    avif: "image/avif",
    ico: "image/vnd.microsoft.icon",
  };
  const fonts = { woff: "font/woff", woff2: "font/woff2", ttf: "font/ttf", otf: "font/otf" };
  const files: Record<string, string | Uint8Array> = {};
  const rules: string[] = [];
  for (const extension of Object.keys(images)) {
    files[`a.${extension}`] = PNG;
    rules.push(`.${extension} { background: url(a.${extension}); }`);
  }
  for (const extension of Object.keys(fonts)) {
    files[`f.${extension}`] = WOFF2;
    rules.push(`@font-face { font-family: F${extension}; src: url(f.${extension}); }`);
  }
  files["theme.css"] = rules.join("\n");
  const result = await compileFiles(files);
  for (const mimeType of [...Object.values(images), ...Object.values(fonts)]) {
    assert.ok(result.cssText.includes(`data:${mimeType};base64,`), mimeType);
  }
});

test("image-set string addresses inline and gradients keep their variables", async () => {
  const result = await compileFiles({
    "theme.css": `.a { background-image: image-set("a.png" 1x, url(b.png) 2x type("image/png")); }
.b { background-image: -webkit-image-set("a.png" 1x); }
.c { background-image: image-set(linear-gradient(var(--from), var(--to)) 1x, "a.png" 2x); }`,
    "a.png": PNG,
    "b.png": PNG,
  });
  assert.equal(result.cssText.includes('"a.png"'), false);
  assert.ok(result.cssText.includes('type("image/png")'));
  assert.ok(result.cssText.includes("linear-gradient(var(--from), var(--to))"));
});

/* ---------- 资源检查 Resource checks ---------- */

test("external and dynamic resource expressions are rejected wherever they appear", async () => {
  const cases: [string, string][] = [
    [":root { --hero: url(https://example.com/a.png); }", "refers outside"],
    [".a { background: url(//example.com/a.png); }", "refers outside"],
    [".a { background: url(ht\\74p://example.com/a.png); }", "refers outside"],
    ['.a { background: \\75 rl("https://example.com/a.png"); }', "refers outside"],
    [".a { background: \\75 rl(https://example.com/a.png); }", "cannot be parsed"],
    ['.a { background: image-set("https://example.com/a.png" 1x); }', "refers outside"],
    ['.a { background: -webkit-image-set("https://example.com/a.png" 1x); }', "refers outside"],
    [
      ":root { --s: 'a.png'; } .a { background: image-set(var(--s) 1x); }",
      "var() as a image-set() image",
    ],
    [".a { background: image-set(url(a.png) var(--more)); }", "var() in a image-set() candidate"],
    ['.a { background: src("https://example.com/a.png"); }', "refers outside"],
    [".a { background: image(var(--s)); }", "var() in image()"],
    [".a { background: url(file:///etc/passwd); }", "refers outside"],
    [".a { background: url(blob:https://example.com/x); }", "refers outside"],
    [".a { background: url(agw-extension://other/a.png); }", "refers outside"],
    [":root { --x: {a:b}; }", "cannot be parsed"],
    [
      '@supports (background: url("https://example.com/a.png")) { .a { color: red; } }',
      "refers outside",
    ],
    [
      "@counter-style x { system: cyclic; symbols: url(https://example.com/a.png); }",
      "refers outside",
    ],
  ];
  for (const [css, message] of cases) {
    await rejectsWith(
      { "theme.css": css, "a.png": PNG },
      message === "cannot be parsed" ? "THEME_PARSE_FAILED" : "THEME_RESOURCE_INVALID",
      message,
    );
  }
});

test("disallowed resource types and positions are rejected", async () => {
  await rejectsWith(
    { "theme.css": ".a { background: url(a.bmp); }", "a.bmp": PNG },
    "THEME_RESOURCE_INVALID",
    "not an allowed image type",
  );
  await rejectsWith(
    { "theme.css": ".a { background: url(a.unknown); }", "a.unknown": PNG },
    "THEME_RESOURCE_INVALID",
  );
  await rejectsWith(
    { "theme.css": ".a { background: url(a.woff2); }", "a.woff2": WOFF2 },
    "THEME_RESOURCE_INVALID",
    "is a font",
  );
  await rejectsWith(
    { "theme.css": "@font-face { font-family: X; src: url(a.png); }", "a.png": PNG },
    "THEME_RESOURCE_INVALID",
    "is an image",
  );
  await rejectsWith(
    { "theme.css": ".a { background: url(data:text/css;base64,LmF7fQ==); }" },
    "THEME_RESOURCE_INVALID",
    '"text/css"',
  );
  await rejectsWith(
    { "theme.css": ".a { background: url('data:text/html,<b>x</b>'); }" },
    "THEME_RESOURCE_INVALID",
    '"text/html"',
  );
  await rejectsWith(
    { "theme.css": '.a { background: url("data:text/javascript,alert(1)"); }' },
    "THEME_RESOURCE_INVALID",
    '"text/javascript"',
  );
  await rejectsWith(
    {
      "theme.css": `@font-face { font-family: X; src: url(data:image/png;base64,${PNG.toString("base64")}); }`,
    },
    "THEME_RESOURCE_INVALID",
    "@font-face source",
  );
  await rejectsWith(
    { "theme.css": ".a { cursor: url(a.cur), auto; }", "a.cur": PNG },
    "THEME_RESOURCE_INVALID",
  );
});

test("SVG images are checked and their package resources inline relative to the SVG", async () => {
  const result = await compileFiles({
    "theme.css": ".a { background: url(icons/frame.svg); }",
    "icons/frame.svg": `<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink">
<style>.f { fill: url(#g); } .p { background: url(pattern.png); }</style>
<defs><linearGradient id="g"><stop offset="0"/></linearGradient></defs>
<image href="pattern.png" width="4" height="4"/>
<use xlink:href="#g"/>
<rect style="fill: url(#g)" fill="url(#g)"/>
</svg>`,
    "icons/pattern.png": PNG,
  });
  const svg = dataUrlBody(result.cssText, "image/svg+xml").toString("utf8");
  assert.equal(svg.includes("pattern.png"), false);
  assert.ok(svg.includes(`data:image/png;base64,${PNG.toString("base64")}`), svg);
  assert.ok(svg.includes('fill="url(#g)"'));
  assert.equal(result.dependencies.length, 3);
});

test("SVG external references, scripts and entities are rejected", async () => {
  const svg = (body: string, head = "") =>
    `${head}<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink">${body}</svg>`;
  const cases: [string, string, string][] = [
    [svg('<image href="https://example.com/a.png"/>'), "THEME_RESOURCE_INVALID", "refers outside"],
    [svg('<use xlink:href="other.svg#x"/>'), "THEME_RESOURCE_INVALID", "only refer to fragments"],
    [
      svg('<rect fill="url(https://example.com/p.svg#x)"/>'),
      "THEME_RESOURCE_INVALID",
      "refers outside",
    ],
    [
      svg('<rect style="fill: url(https://example.com/p.svg#x)"/>'),
      "THEME_RESOURCE_INVALID",
      "refers outside",
    ],
    [
      svg("<style>@import url(https://example.com/a.css);</style>"),
      "THEME_IMPORT_INVALID",
      "not expanded",
    ],
    [
      svg("<style>.a { background: url(https://example.com/a.png); }</style>"),
      "THEME_RESOURCE_INVALID",
      "refers outside",
    ],
    [svg("<script>alert(1)</script>"), "THEME_RESOURCE_INVALID", "<script>"],
    [svg('<rect onclick="alert(1)"/>'), "THEME_RESOURCE_INVALID", "event attributes"],
    [
      svg('<foreignObject><div xmlns="http://www.w3.org/1999/xhtml"/></foreignObject>'),
      "THEME_RESOURCE_INVALID",
      "<foreignObject>",
    ],
    [
      svg("", '<!DOCTYPE svg [<!ENTITY ext SYSTEM "file:///etc/passwd">]>'),
      "THEME_RESOURCE_INVALID",
      "DOCTYPE",
    ],
    [
      svg("", '<?xml-stylesheet href="https://example.com/a.css"?>'),
      "THEME_RESOURCE_INVALID",
      "processing instructions",
    ],
    [svg('<rect fill="url(a.woff2)"/>'), "THEME_RESOURCE_INVALID", "is a font"],
  ];
  for (const [source, code, message] of cases) {
    await rejectsWith(
      { "theme.css": ".a { background: url(a.svg); }", "a.svg": source, "a.woff2": WOFF2 },
      code,
      message,
    );
  }
  // 未声明的实体引用是 XML 解析错误。An undeclared entity reference is an XML parse error.
  await rejectsWith(
    { "theme.css": ".a { background: url(a.svg); }", "a.svg": svg("<text>&ext;</text>") },
    "THEME_PARSE_FAILED",
    "The SVG cannot be parsed",
  );
  await rejectsWith(
    {
      "theme.css": ".a { background: url(a.svg); }",
      "a.svg": svg('<image href="b.svg"/>'),
      "b.svg": svg('<image href="a.svg"/>'),
    },
    "THEME_RESOURCE_INVALID",
    "refers to itself",
  );
});

test("data URL SVGs are checked and cannot use relative references", async () => {
  const inline = (body: string) =>
    `data:image/svg+xml,${encodeURIComponent(`<svg xmlns="http://www.w3.org/2000/svg">${body}</svg>`)}`;
  const ok = await compileFiles({
    "theme.css": `.a { background: url("${inline('<rect fill="url(#g)"/>')}"); }`,
  });
  assert.ok(ok.cssText.includes("data:image/svg+xml,"));
  await rejectsWith(
    { "theme.css": `.a { background: url("${inline('<image href="a.png"/>')}"); }` },
    "THEME_RESOURCE_INVALID",
    "relative reference inside a data URL",
  );
  await rejectsWith(
    {
      "theme.css": `.a { background: url("${inline('<image href="https://example.com/a.png"/>')}"); }`,
    },
    "THEME_RESOURCE_INVALID",
    "refers outside",
  );
});

test("@namespace URIs are namespace names and need an entry without imports", async () => {
  const result = await compileFiles({
    "theme.css": "@namespace svg url(http://www.w3.org/2000/svg);\nsvg|rect { fill: red; }",
  });
  assert.ok(result.cssText.includes("@namespace svg url(http://www.w3.org/2000/svg)"));
  await rejectsWith(
    {
      "theme.css": '@import "a.css";\n@namespace svg url(http://www.w3.org/2000/svg);',
      "a.css": "",
    },
    "THEME_IMPORT_INVALID",
    "cannot use @import",
  );
  await rejectsWith(
    { "theme.css": '@import "a.css";', "a.css": '@namespace svg "http://www.w3.org/2000/svg";' },
    "THEME_IMPORT_INVALID",
    "@namespace can only appear in a theme entry without @import",
  );
});

/* ---------- 失败路径 Failures ---------- */

test("structure errors, missing files and paths outside the package report their source", async () => {
  await rejectsWith({ "theme.css": ".a { color: red;\n.b {" }, "THEME_PARSE_FAILED", "theme.css:");
  await rejectsWith(
    { "theme.css": '@import "a.css";', "a.css": "\n\n.a { color: red; " },
    "THEME_PARSE_FAILED",
    "a.css:3:",
  );
  await rejectsWith(
    { "theme.css": ".a { background: url(missing.png); }" },
    "THEME_FILE_NOT_FOUND",
    '"missing.png" does not exist',
    "theme.css:1:6",
  );
  await rejectsWith(
    { "theme.css": ".a { background: url(../../a.png); }" },
    "PACKAGE_PATH_OUTSIDE_ROOT",
  );
  await rejectsWith(
    { "theme.css": ".a { background: url(a\\\\b.png); }" },
    "THEME_RESOURCE_INVALID",
    "backslash",
  );
  await rejectsWith(
    { "theme.css": ".a { background: url(%zz.png); }" },
    "THEME_RESOURCE_INVALID",
    "percent-encoding",
  );
  await rejectsWith(
    { "theme.css": Buffer.from([0x2e, 0x61, 0xff, 0x7b, 0x7d]) },
    "THEME_PARSE_FAILED",
    "UTF-8",
  );
  await rejectsWith({ "theme.css": '@charset "iso-8859-1";\n.a{}' }, "THEME_PARSE_FAILED", "UTF-8");
});

test("symbolic links pointing outside the package are rejected", async () => {
  const scratch = await createScratchDirectory("theme-css");
  try {
    const root = join(scratch.path, "theme");
    await writeFiles(scratch.path, {
      "outside.png": PNG,
      "outside.css": ".x { color: red; }",
      "theme/theme.css": '@import "linked.css";\n.a { background: url(linked.png); }',
    });
    await symlink(join(scratch.path, "outside.css"), join(root, "linked.css"));
    await symlink(join(scratch.path, "outside.png"), join(root, "linked.png"));
    await assert.rejects(compileCssTheme(await realpath(root), "theme.css"), (error: unknown) => {
      assert.ok(error instanceof ThemeCompileError);
      assert.equal(error.code, "PACKAGE_PATH_OUTSIDE_ROOT");
      assert.ok(error.message.includes('"linked.css" resolves outside'), error.message);
      return true;
    });
  } finally {
    await scratch.dispose();
  }
});

/* ---------- 体积上限 Size limits ---------- */

test("a CSS file may be exactly 1 MiB", async () => {
  const exact = `/*${"x".repeat(MAX_THEME_CSS_FILE_BYTES - 4)}*/`;
  assert.equal(Buffer.byteLength(exact), MAX_THEME_CSS_FILE_BYTES);
  await compileFiles({ "theme.css": exact });
  await rejectsWith(
    { "theme.css": `${exact} ` },
    "THEME_SIZE_LIMIT_EXCEEDED",
    "theme.css",
    `${MAX_THEME_CSS_FILE_BYTES + 1} bytes`,
    "1 MiB",
  );
});

test("an image or font may decode to exactly 4 MiB", async () => {
  await compileFiles({
    "theme.css": ".a { background: url(a.png); }",
    "a.png": Buffer.alloc(MAX_THEME_ASSET_BYTES),
  });
  await rejectsWith(
    {
      "theme.css": ".a { background: url(a.png); }",
      "a.png": Buffer.alloc(MAX_THEME_ASSET_BYTES + 1),
    },
    "THEME_SIZE_LIMIT_EXCEEDED",
    '"a.png"',
    "4 MiB",
  );
});

test("decoded data URLs count toward the input total", async () => {
  // 每个 CSS 文件带一个解码后 700 KiB 的 data URL：文件约 0.91 MiB，五个文件与它们的 data URL 合计约 7.98 MiB，
  // 六个合计超过 8 MiB；只读取文件时远低于上限。
  // Each CSS file carries a data URL decoding to 700 KiB: files are about 0.91 MiB, five of them with their data URLs
  // total about 7.98 MiB and six exceed 8 MiB, while the files alone stay far below the limit.
  const dataUrl = `data:image/png;base64,${Buffer.alloc(700 * 1024).toString("base64")}`;
  const theme = (count: number) => {
    const files: Record<string, string> = { "theme.css": "" };
    for (let index = 0; index < count; index += 1) {
      files[`${index}.css`] = `.d${index} { background: url(${dataUrl}); }`;
      files["theme.css"] += `@import "${index}.css";\n`;
    }
    return files;
  };
  await compileFiles(theme(5));
  await rejectsWith(theme(6), "THEME_SIZE_LIMIT_EXCEEDED", "A data URL brings", "8 MiB");
});

test("input files and data URLs total at most 8 MiB", async () => {
  const css = ".a { background: url(a.png); } .b { background: url(b.png); }";
  const second = MAX_THEME_INPUT_BYTES - MAX_THEME_ASSET_BYTES - Buffer.byteLength(css);
  await compileFiles({
    "theme.css": css,
    "a.png": Buffer.alloc(MAX_THEME_ASSET_BYTES),
    "b.png": Buffer.alloc(second),
  });
  await rejectsWith(
    {
      "theme.css": css,
      "a.png": Buffer.alloc(MAX_THEME_ASSET_BYTES),
      "b.png": Buffer.alloc(second + 1),
    },
    "THEME_SIZE_LIMIT_EXCEEDED",
    '"b.png" brings',
    `${MAX_THEME_INPUT_BYTES + 1} bytes`,
  );
});

test("a theme reads at most 256 files", async () => {
  const files: Record<string, string> = {};
  const imports: string[] = [];
  for (let index = 0; index < MAX_THEME_FILES - 1; index += 1) {
    files[`parts/${index}.css`] = `.p${index} { color: red; }`;
    imports.push(`@import "parts/${index}.css";`);
  }
  await compileFiles({ ...files, "theme.css": imports.join("\n") });
  files[`parts/${MAX_THEME_FILES - 1}.css`] = ".last { color: red; }";
  imports.push(`@import "parts/${MAX_THEME_FILES - 1}.css";`);
  await rejectsWith(
    { ...files, "theme.css": imports.join("\n") },
    "THEME_SIZE_LIMIT_EXCEEDED",
    `would be file ${MAX_THEME_FILES + 1}`,
  );
});

test("imports nest at most 32 levels and expand at most 256 times, repeated imports included", async () => {
  const chain = (depth: number) => {
    const files: Record<string, string> = { "theme.css": '@import "0.css";' };
    for (let index = 0; index < depth; index += 1) {
      files[`${index}.css`] =
        index + 1 < depth ? `@import "${index + 1}.css";` : ".end { color: red; }";
    }
    return files;
  };
  await compileFiles(chain(MAX_THEME_IMPORT_DEPTH));
  await rejectsWith(
    chain(MAX_THEME_IMPORT_DEPTH + 1),
    "THEME_SIZE_LIMIT_EXCEEDED",
    `import depth ${MAX_THEME_IMPORT_DEPTH + 1}`,
  );

  const repeated = (count: number) => ({
    "theme.css": Array.from({ length: count }, () => '@import "a.css";').join("\n"),
    "a.css": ".a { color: red; }",
  });
  const result = await compileFiles(repeated(MAX_THEME_IMPORT_EXPANSIONS));
  assert.equal(result.cssText.match(/\.a \{/gu)?.length, MAX_THEME_IMPORT_EXPANSIONS);
  await rejectsWith(
    repeated(MAX_THEME_IMPORT_EXPANSIONS + 1),
    "THEME_SIZE_LIMIT_EXCEEDED",
    `import expansion ${MAX_THEME_IMPORT_EXPANSIONS + 1}`,
  );
});

test("the processed text, base64 data included, stays within 16 MiB", async () => {
  // 3 MiB 图片编码后约 4 MiB，引用五次超过 16 MiB；输入只计一次。A 3 MiB image encodes to about 4 MiB; five references
  // exceed 16 MiB while the input counts once.
  const rules = Array.from(
    { length: 5 },
    (_, index) => `.r${index} { background: url(a.png); }`,
  ).join("\n");
  await rejectsWith(
    { "theme.css": rules, "a.png": Buffer.alloc(3 * 1024 * 1024) },
    "THEME_SIZE_LIMIT_EXCEEDED",
    "The theme CSS would be",
    "16 MiB",
  );
  const three = rules.split("\n").slice(0, 3).join("\n");
  const result = await compileFiles({ "theme.css": three, "a.png": Buffer.alloc(3 * 1024 * 1024) });
  assert.ok(result.byteLength > 12 * 1024 * 1024);
});

/* ---------- 最终文本 Final text ---------- */

test("the final text check covers selectors, escapes and dynamic image-set candidates", () => {
  verifyThemeCssText(`.a { background: url(data:image/png;base64,AA==); filter: url(#f); }`);
  verifyThemeCssText(`@namespace svg url(http://www.w3.org/2000/svg);`);
  verifyThemeCssText(`.a { background: image-set(linear-gradient(var(--a), red) 1x); }`);
  for (const css of [
    '@import "a.css";',
    "@\\69 mport 'a.css';",
    ".a { background: url(https://example.com/a.png); }",
    ".a[data-x=url(https://example.com)] {}",
    '.a { background: image-set("https://example.com/a.png" 1x); }',
    ".a { background: image-set(var(--x) 1x); }",
    ".a { background: \\75 rl(https://example.com/a.png); }",
    ".a { background: url(data:text/css,x); }",
    ".a { background: src(var(--x)); }",
  ]) {
    assert.throws(() => verifyThemeCssText(css), ThemeCompileError, css);
  }
});
