import assert from "node:assert/strict";
import { resolveObjectURL } from "node:buffer";
import { after, beforeEach, test } from "node:test";

import { createExtensionError } from "@agw/extension-sdk";

import { connectTestHost } from "../test-host";
import { createImageObjectUrl } from "./image-source";
import { classifyImage } from "./markdown-links";

const FILE = "agw-cloud://local/project-1/workspace/docs/guide.md";
const LOGO = "agw-cloud://local/project-1/workspace/images/logo.png";
const PNG_BYTES = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

const reads: string[] = [];
// 设置后，读取等到页面取消请求才结束。When set, a read ends only when the page cancels the request.
let holdReads = false;

const host = connectTestHost(async (method, params, signal) => {
  assert.equal(method, "fs.readFile");
  const { uri } = params as { uri: string };
  reads.push(uri);
  if (holdReads) {
    await new Promise((_, reject) => signal.addEventListener("abort", reject, { once: true }));
  }
  if (uri !== LOGO) throw createExtensionError("ENOENT", `${uri} does not exist.`);
  // 回复会转移 buffer，每次返回新的数组。The reply transfers the buffer, so each read returns a new array.
  return new Uint8Array(PNG_BYTES);
});
after(() => host.close());
beforeEach(() => {
  reads.length = 0;
  holdReads = false;
});

function relativeImage(src: string): string {
  const source = classifyImage(FILE, src);
  assert.equal(source.kind, "file");
  return source.uri;
}

test("a relative image becomes a Blob URL of the bytes fs.readFile returns, typed by its extension", async () => {
  const uri = relativeImage("../images/logo.png");
  const url = await createImageObjectUrl(host.api.fs, uri, new AbortController().signal);
  assert.deepEqual(reads, [LOGO]);
  assert.match(url, /^blob:/u);
  const blob = resolveObjectURL(url);
  assert.ok(blob);
  assert.equal(blob.type, "image/png");
  assert.deepEqual([...new Uint8Array(await blob.arrayBuffer())], PNG_BYTES);
  URL.revokeObjectURL(url);
  assert.equal(resolveObjectURL(url), undefined);
});

test("a failed read rejects with the read error and creates no URL", async (t) => {
  const created = t.mock.method(URL, "createObjectURL");
  await assert.rejects(
    createImageObjectUrl(host.api.fs, relativeImage("missing.png"), new AbortController().signal),
    { name: "ExtensionError", code: "ENOENT" },
  );
  assert.equal(created.mock.callCount(), 0);
});

test("an aborted signal rejects without creating a URL, before, during and right after the read", async (t) => {
  const created = t.mock.method(URL, "createObjectURL");
  const uri = relativeImage("../images/logo.png");

  // 调用前已中止：请求不发出。Aborted before the call: no request is sent.
  const before = new AbortController();
  before.abort();
  await assert.rejects(createImageObjectUrl(host.api.fs, uri, before.signal), {
    name: "ExtensionError",
    code: "ABORT_ERR",
  });
  assert.deepEqual(reads, []);

  // 宿主还在读取时中止。Aborted while the host is still reading.
  holdReads = true;
  const during = new AbortController();
  const pending = createImageObjectUrl(host.api.fs, uri, during.signal);
  while (reads.length === 0) await new Promise((resolve) => setTimeout(resolve, 5));
  during.abort();
  await assert.rejects(pending, { name: "ExtensionError", code: "ABORT_ERR" });
  holdReads = false;

  // 回复到达之后、读取结果处理之前中止：端口上后注册的监听在 SDK 处理回复之后、微任务之前运行。
  // Aborted after the reply arrived and before its result is handled: a listener registered after the SDK's runs
  // after the SDK handled the reply and before microtasks.
  // 被取消的请求随后也有一个失败回复，只看成功的回复。The canceled request still gets a failed reply; only a success counts.
  const afterReply = new AbortController();
  const abortOnReply = (event: MessageEvent<{ kind: string; ok?: boolean }>) => {
    if (event.data.kind !== "response" || !event.data.ok) return;
    host.pagePort.removeEventListener("message", abortOnReply);
    afterReply.abort();
  };
  host.pagePort.addEventListener("message", abortOnReply);
  await assert.rejects(createImageObjectUrl(host.api.fs, uri, afterReply.signal), {
    name: "AbortError",
  });
  assert.deepEqual(reads, [LOGO, LOGO]);

  assert.equal(created.mock.callCount(), 0);
});
