import assert from "node:assert/strict";
import test from "node:test";

import { classifyImage, classifyLink, imageMimeType, resolveRelativeUri } from "./markdown-links";

const ROOT = "agw-cloud://server-1/project-1/workspace/";
const FILE = `${ROOT}docs/guide.md`;

test("resolveRelativeUri resolves against the file's directory and drops the query and fragment", () => {
  assert.equal(resolveRelativeUri(FILE, "notes.md"), `${ROOT}docs/notes.md`);
  assert.equal(resolveRelativeUri(FILE, "./sub/notes.md"), `${ROOT}docs/sub/notes.md`);
  assert.equal(resolveRelativeUri(FILE, "../images/logo.png"), `${ROOT}images/logo.png`);
  assert.equal(resolveRelativeUri(FILE, "logo.png?raw=1#top"), `${ROOT}docs/logo.png`);
  assert.equal(resolveRelativeUri(FILE, "my%20notes.md"), `${ROOT}docs/my%20notes.md`);
  assert.equal(resolveRelativeUri(FILE, "图片 1.png"), `${ROOT}docs/%E5%9B%BE%E7%89%87%201.png`);
  assert.equal(resolveRelativeUri(FILE, ".."), ROOT);
});

test("resolveRelativeUri keeps an additional directory as the root", () => {
  const file = "agw-cloud://server-1/project-1/dir-42/readme.md";
  assert.equal(
    resolveRelativeUri(file, "assets/a.png"),
    "agw-cloud://server-1/project-1/dir-42/assets/a.png",
  );
  assert.equal(resolveRelativeUri(file, "../workspace/a.png"), null);
});

test("resolveRelativeUri returns null for references that leave the root", () => {
  for (const reference of [
    "../../outside.md",
    "../../../outside.md",
    "%2e%2e/%2E%2e/outside.md",
    "/absolute.md",
    "//other-server/project-1/workspace/a.md",
    "..%2f..%2foutside.md",
    "..%5C..%5Coutside.md",
    "a\\..\\..\\outside.md",
  ]) {
    assert.equal(resolveRelativeUri(FILE, reference), null, reference);
  }
});

test("resolveRelativeUri returns null for references with a scheme", () => {
  assert.equal(resolveRelativeUri(FILE, "https://example.com/a.md"), null);
  assert.equal(resolveRelativeUri(FILE, `${ROOT}docs/a.md`), null);
});

test("resolveRelativeUri rejects a file URI that is not a cloud file", () => {
  assert.throws(() => resolveRelativeUri("file:///Users/me/a.md", "b.md"), /Not a cloud file URI/);
  assert.throws(
    () => resolveRelativeUri("agw-cloud://server-1/project-1", "b.md"),
    /Not a cloud file URI/,
  );
});

test("classifyLink returns decoded anchors", () => {
  assert.deepEqual(classifyLink(FILE, "#getting-started"), {
    kind: "anchor",
    id: "getting-started",
  });
  assert.deepEqual(classifyLink(FILE, "#%E4%B8%AD%E6%96%87-%E6%A0%87%E9%A2%98"), {
    kind: "anchor",
    id: "中文-标题",
  });
});

test("classifyLink opens relative files inside the root", () => {
  assert.deepEqual(classifyLink(FILE, "../readme.md"), { kind: "file", uri: `${ROOT}readme.md` });
  assert.deepEqual(classifyLink(FILE, "other.md#section"), {
    kind: "file",
    uri: `${ROOT}docs/other.md`,
  });
  assert.deepEqual(classifyLink(FILE, "../../outside.md"), { kind: "ignored" });
  assert.deepEqual(classifyLink(FILE, "//other-server/a.md"), { kind: "ignored" });
});

test("classifyLink sends only http and https links to the browser", () => {
  assert.deepEqual(classifyLink(FILE, "https://example.com/a?b=1#c"), {
    kind: "external",
    url: "https://example.com/a?b=1#c",
  });
  assert.deepEqual(classifyLink(FILE, "HTTP://example.com"), {
    kind: "external",
    url: "HTTP://example.com",
  });
  for (const href of [
    "mailto:someone@example.com",
    "ftp://example.com/file",
    "javascript:alert(1)",
    `${ROOT}docs/a.md`,
    "",
  ]) {
    assert.deepEqual(classifyLink(FILE, href), { kind: "ignored" }, href);
  }
});

test("classifyImage separates data, project and external images", () => {
  assert.deepEqual(classifyImage(FILE, "data:image/png;base64,AAAA"), {
    kind: "data",
    src: "data:image/png;base64,AAAA",
  });
  assert.deepEqual(classifyImage(FILE, "DATA:image/gif;base64,AAAA"), {
    kind: "data",
    src: "DATA:image/gif;base64,AAAA",
  });
  assert.deepEqual(classifyImage(FILE, "../images/logo.png"), {
    kind: "file",
    uri: `${ROOT}images/logo.png`,
  });
  for (const src of [
    "https://example.com/logo.png",
    "blob:agw-extension://markdown-preview/1234",
    "//cdn.example.com/logo.png",
    "../../outside.png",
    "",
  ]) {
    assert.deepEqual(classifyImage(FILE, src), { kind: "external" }, src);
  }
});

test("imageMimeType maps image extensions case-insensitively", () => {
  assert.equal(imageMimeType(`${ROOT}a.png`), "image/png");
  assert.equal(imageMimeType(`${ROOT}a.JPG`), "image/jpeg");
  assert.equal(imageMimeType(`${ROOT}a.jpeg`), "image/jpeg");
  assert.equal(imageMimeType(`${ROOT}a.gif`), "image/gif");
  assert.equal(imageMimeType(`${ROOT}dir.v2/a.svg`), "image/svg+xml");
  assert.equal(imageMimeType(`${ROOT}a.webp`), "image/webp");
  assert.equal(imageMimeType(`${ROOT}a.avif`), "image/avif");
  assert.equal(imageMimeType(`${ROOT}a.bmp`), null);
  assert.equal(imageMimeType(`${ROOT}dir.png/logo`), null);
});
