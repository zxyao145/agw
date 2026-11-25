import type { TestContext } from "node:test";
import { createScratchDirectory } from "@desktop/main/scratch-directory.test-helper";
import { WorkspaceTabsFile } from "@desktop/main/settings/workspace-tabs-file";
import { WorkspaceTabsStore } from "./workspace-tabs-store";

export async function createTestTabs(t: TestContext, onActivate?: () => void) {
  const scratch = await createScratchDirectory("workspace-layout");
  const file = new WorkspaceTabsFile(scratch.path);
  const scope = { serverId: "local", projectId: "project" };
  t.after(async () => {
    await file.read(scope.serverId, scope.projectId);
    await scratch.dispose();
  });
  const tabs = new WorkspaceTabsStore({
    bridge: {
      getWorkspaceTabs: file.read.bind(file),
      saveWorkspaceTabs: file.save.bind(file),
    },
    resources: {
      confirmDiscard: async () => true,
      release: () => undefined,
      discardDrafts: () => undefined,
    },
    reportError: (_message, error) => {
      throw error;
    },
    onActivate,
  });
  await tabs.setScope(scope);
  return tabs;
}
