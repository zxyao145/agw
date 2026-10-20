import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment, startApiServer } from "@agw/test-harness";

const { React, act, fireEvent, render, screen, waitFor } = await setupDomEnvironment();
const { configureApiRuntime, resetApiRuntime } = await import("@agw/api");
const { default: Explorer } = await import("./explorer.tsx");

let srcChildren = [{ name: "a.ts", path: "src/a.ts", type: "file", size: 1 }];
let rootItems = [
  { name: "src", path: "src", type: "directory" },
  { name: "README.md", path: "README.md", type: "file", size: 12 },
];
const api = await startApiServer({
  "GET /api/files/list": (request: { query: URLSearchParams }) => ({
    items: request.query.get("path") === "src" ? srcChildren : rootItems,
  }),
});
configureApiRuntime({ baseUrl: api.baseUrl, token: null });
test.after(() => resetApiRuntime());

function listRequests(path: string | null): number {
  return api.requests.filter(
    (request) => request.path === "/api/files/list" && (request.query.get("path") ?? null) === path,
  ).length;
}

test.beforeEach(() => {
  api.requests.length = 0;
});

function explorer(refreshSignal?: number) {
  return React.createElement(Explorer, {
    projectId: "project-1",
    rootDirectory: "/work",
    onlyDiff: false,
    recursiveMode: false,
    refreshSignal,
    onFileDeleted() {},
    onFileSelected() {},
    onFileReseted() {},
    onFileGitScopeChanged() {},
  });
}

async function toggle(name: string) {
  await act(async () => {
    fireEvent.click(screen.getByText(name));
  });
}

test("Explorer_RefreshSignalChanges_ReloadsRootAndExpandedDirectories", async () => {
  srcChildren = [{ name: "a.ts", path: "src/a.ts", type: "file", size: 1 }];
  const view = render(explorer(0));
  await screen.findByText("src");
  await toggle("src");
  await screen.findByText("a.ts");
  assert.equal(listRequests(null), 1);
  assert.equal(listRequests("src"), 1);

  // 第一次的值之外没有变化时不读取。An unchanged signal reads nothing.
  view.rerender(explorer(0));
  await act(() => new Promise((resolve) => setTimeout(resolve, 300)));
  assert.equal(listRequests(null), 1);
  assert.equal(listRequests("src"), 1);

  srcChildren = [{ name: "b.ts", path: "src/b.ts", type: "file", size: 1 }];
  view.rerender(explorer(1));
  await waitFor(() => assert.ok(screen.queryByText("b.ts")));
  assert.equal(screen.queryByText("a.ts"), null);
  await waitFor(() => assert.equal(listRequests(null), 2));
  assert.equal(listRequests("src"), 2);
});

test("Explorer_RefreshSignalChanges_CollapsedDirectoryReadsOnNextExpand", async () => {
  srcChildren = [{ name: "a.ts", path: "src/a.ts", type: "file", size: 1 }];
  const view = render(explorer(0));
  await screen.findByText("src");
  await toggle("src");
  await screen.findByText("a.ts");
  await toggle("src");
  assert.equal(screen.queryByText("a.ts"), null);
  const rootReads = listRequests(null);
  const srcReads = listRequests("src");

  srcChildren = [{ name: "b.ts", path: "src/b.ts", type: "file", size: 1 }];
  view.rerender(explorer(1));
  await waitFor(() => assert.equal(listRequests(null), rootReads + 1));
  assert.equal(listRequests("src"), srcReads, "a collapsed directory is not read");

  await toggle("src");
  await screen.findByText("b.ts");
  assert.equal(screen.queryByText("a.ts"), null);
  assert.equal(listRequests("src"), srcReads + 1);
});

test("Explorer_FirstRefreshSignal_DoesNotReload", async () => {
  const view = render(explorer(5));
  await screen.findByText("src");
  const rootReads = listRequests(null);

  view.rerender(explorer(5));
  await act(() => new Promise((resolve) => setTimeout(resolve, 300)));

  assert.equal(listRequests(null), rootReads);
});
