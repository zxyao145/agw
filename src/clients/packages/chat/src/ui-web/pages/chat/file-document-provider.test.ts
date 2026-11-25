import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";
import type { FileDocumentContextValue, FileDocumentProviderProps } from "./file-document-provider";
import type { ChatProjectContextValue } from "./chat-project-provider";

const { React, act, render, waitFor } = await setupDomEnvironment();
const { providerFixture } = await import("./provider-test-fixture");
const { useChatProject } = await import("./chat-project-provider");
const { FileDocumentProvider, useFileDocument } = await import("./file-document-provider");
let file: FileDocumentContextValue;
let project: ChatProjectContextValue;
function Probe() {
  file = useFileDocument();
  project = useChatProject();
  return null;
}
function documentWith(props: Partial<FileDocumentProviderProps> = {}) {
  return React.createElement(FileDocumentProvider, {
    directoryId: null,
    path: "file.txt",
    diffScope: "staged",
    diffFileView: false,
    onDiffFileViewChange() {},
    readSourceContent: true,
    children: React.createElement(Probe),
    ...props,
  });
}

test("the file document reads diff or content by onlyDiff and diffFileView", async (t) => {
  const { wrap, server } = await providerFixture(t, {
    "GET /api/files/read": "text",
    "GET /api/files/diff": { diff: "+text" },
  });
  const view = render(wrap(documentWith()));
  await waitFor(() => assert.equal(file.fileContent, "text"));
  act(() => project.setOnlyDiff(true));
  await waitFor(() => assert.equal(file.diffContentData?.diff, "+text"));
  assert.equal(
    server.requests.find((r) => r.path === "/api/files/diff")?.query.get("scope"),
    "staged",
  );
  view.rerender(wrap(documentWith({ diffFileView: true })));
  await waitFor(() => assert.equal(file.fileContent, "text"));
  view.rerender(wrap(documentWith({ diffFileView: true, readSourceContent: false })));
  assert.equal(file.fileContent, "");
  assert.equal(server.requests.filter((r) => r.path === "/api/files/read").length, 2);
});

test("a write notification for the same path reads again and other paths do not", async (t) => {
  let reads = 0;
  const { wrap } = await providerFixture(t, { "GET /api/files/read": () => String(++reads) });
  render(wrap(documentWith()));
  await waitFor(() => assert.equal(file.fileContent, "1"));
  act(() => {
    project.notifyFileWritten(null, "other.txt");
    project.notifyFileWritten("d2", "file.txt");
  });
  assert.equal(reads, 1);
  act(() => project.notifyFileWritten(null, "file.txt"));
  await waitFor(() => assert.equal(file.fileContent, "2"));
  act(() => project.notifyFileReset("file.txt"));
  await waitFor(() => assert.equal(file.fileContent, "3"));
});

test("a directory outside the Project shows the directory error", async (t) => {
  const { wrap, server } = await providerFixture(t);
  render(wrap(documentWith({ directoryId: "removed" })));
  await waitFor(() => assert.equal(project.selectedProjectId, "a"));
  assert.equal(file.contentError, "The directory is no longer part of this Project.");
  assert.ok(!server.requests.some((r) => r.path === "/api/files/read"));
});

test("mounting a selected file under StrictMode reads it once", async (t) => {
  const { wrap, server } = await providerFixture(t, { "GET /api/files/read": "text" });
  function SelectedFile() {
    return useChatProject().selectedProjectId ? documentWith() : null;
  }
  render(React.createElement(React.StrictMode, null, wrap(React.createElement(SelectedFile))));
  await waitFor(() => assert.equal(file.fileContent, "text"));
  assert.equal(server.requests.filter((r) => r.path === "/api/files/read").length, 1);
});
