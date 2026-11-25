import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";
import type { ChatProjectContextValue } from "./chat-project-provider";

const { React, render, waitFor, act, screen } = await setupDomEnvironment();
const { providerFixture } = await import("./provider-test-fixture");
const { useChatProject } = await import("./chat-project-provider");
let project: ChatProjectContextValue;
function Probe() {
  project = useChatProject();
  return null;
}

test("selecting and accepting conversations refreshes the active project's status without a URL change", async (t) => {
  const { wrap, server } = await providerFixture(t);
  const probe = React.createElement(Probe);
  const view = render(wrap(probe, "a", null));
  const reads = () =>
    server.requests.filter((r) => r.path === "/api/projects/conversation-activity").length;
  await waitFor(() => assert.equal(reads(), 1));
  view.rerender(wrap(probe, "a", "opened"));
  await waitFor(() => assert.equal(reads(), 2));
  view.rerender(wrap(probe, "a", null));
  view.rerender(wrap(probe, "a", "accepted"));
  await waitFor(() => assert.equal(reads(), 3));
});

test("an outdated project list is refreshed before it can prune stored tabs", async (t) => {
  const { wrap, client } = await providerFixture(t);
  client.setQueryData(["projects"], [{ id: "a", name: "A" }]);
  const snapshots: { loaded: boolean; projects: string[] }[] = [];
  function ProjectList() {
    const value = useChatProject();
    snapshots.push({ loaded: value.projectsLoaded, projects: value.projects.map((p) => p.id) });
    return null;
  }
  render(wrap(React.createElement(ProjectList)));
  await waitFor(() => assert.deepEqual(snapshots.at(-1), { loaded: true, projects: ["a", "b"] }));
  assert.deepEqual(snapshots[0], { loaded: false, projects: ["a"] });
});

test("switching Projects keeps each Project's directory and pending comments", async (t) => {
  const { wrap } = await providerFixture(t);
  const probe = React.createElement(Probe);
  const view = render(wrap(probe));
  await waitFor(() => assert.equal(project.selectedProjectId, "a"));
  const comment = {
    id: "note",
    side: "current" as const,
    filePath: "x",
    lineNumber: 1,
    content: "note",
    timestamp: new Date(0),
  };
  act(() => {
    project.selectDirectory("d2");
    project.setComments([comment]);
  });
  view.rerender(wrap(probe, "b"));
  assert.equal(project.selectedProjectId, "b");
  assert.deepEqual(project.comments, []);
  view.rerender(wrap(probe));
  assert.equal(project.selectedDirectory?.id, "d2");
  assert.deepEqual(project.comments, [comment]);
});

test("notifyFileWritten bumps the tree signal and reaches subscribers", async (t) => {
  const { wrap } = await providerFixture(t);
  render(wrap(React.createElement(Probe)));
  await waitFor(() => assert.equal(project.selectedProjectId, "a"));
  const changes: unknown[] = [];
  const unsubscribe = project.subscribeFileChanges((change) => changes.push(change));
  act(() => project.notifyFileWritten(null, "demo.txt"));
  assert.equal(project.fileTreeRefreshSignal, 1);
  unsubscribe();
  act(() => project.notifyFileReset("demo.txt"));
  assert.deepEqual(changes, [{ kind: "written", directoryId: null, path: "demo.txt" }]);
});

test("the reconnect dialog shows the latest non-null report", async (t) => {
  const { wrap } = await providerFixture(t);
  render(wrap(React.createElement(Probe)));
  await waitFor(() => assert.equal(project.selectedProjectId, "a"));
  const retry = async () => {};
  act(() => {
    project.reportReconnectState(
      "one",
      { status: "reconnecting", retryAttempt: 6, retryDelayMs: 0 },
      retry,
    );
    project.reportReconnectState(
      "two",
      { status: "reconnecting", retryAttempt: 8, retryDelayMs: 0 },
      retry,
    );
  });
  assert.match(screen.getByRole("dialog").textContent!, /3\/5/);
  act(() => project.reportReconnectState("two", null, retry));
  assert.match(screen.getByRole("dialog").textContent!, /1\/5/);
  act(() => project.reportReconnectState("one", null, retry));
  assert.equal(project.isReconnecting, false);
  assert.equal(screen.queryByRole("dialog"), null);
});
