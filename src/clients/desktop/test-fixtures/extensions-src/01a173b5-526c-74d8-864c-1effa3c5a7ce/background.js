// sample 的 background：记录钩子调用顺序与 context 变化，执行命令，跟踪执行会话的关闭。
// Background of the sample fixture: records hook calls and context changes, runs commands and tracks session closing.
import { defineBackground } from "/__agw/sdk.js";
import { appendJson, createSessionTracker, writeJson } from "./shared.js";

let api = null;
let tracker = null;

defineBackground({
  async onInstalled({ reason, previousVersion }, connectedApi) {
    window.api = connectedApi;
    await appendJson(connectedApi, "hooks.json", `onInstalled:${reason}:${previousVersion}`);
  },

  async activate(connectedApi) {
    window.api = connectedApi;
    api = connectedApi;
    tracker = createSessionTracker(api);
    window.openTrackedSession = tracker.open;
    api.context.onDidChange((context) => {
      void appendJson(api, "context-log.json", {
        project: context.project,
        conversation: context.conversation,
        user: context.user,
      });
    });
    await appendJson(api, "hooks.json", "activate");
  },

  async deactivate() {
    const hookAt = Date.now();
    await appendJson(api, "hooks.json", "deactivate");
    await tracker.writeReport("background-close.json", hookAt);
  },

  commands: {
    "insert-greeting": async (args, connectedApi) => {
      await connectedApi.chat.composer.insertText("Hello from sample");
    },
    // 文件浏览器右键菜单的参数为 { projectId, uri, name, type }。Explorer arguments are { projectId, uri, name, type }.
    "read-file": async (args, connectedApi) => {
      const text = await connectedApi.fs.readTextFile(args.uri);
      await writeJson(connectedApi, "explorer-read.json", { uri: args.uri, text });
    },
  },
});
