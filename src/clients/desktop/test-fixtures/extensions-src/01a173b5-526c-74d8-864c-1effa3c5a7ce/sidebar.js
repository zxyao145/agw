// sample 的侧栏视图：跟踪执行会话，卸载时把关闭记录写入存储目录。
// Sidebar view of the sample fixture: tracks sessions and saves the close records to storage on unload.
import { defineView } from "/__agw/sdk.js";
import { createSessionTracker } from "./shared.js";

let tracker = null;

defineView({
  onLoad(api) {
    window.api = api;
    tracker = createSessionTracker(api);
    window.openTrackedSession = tracker.open;
  },

  async onUnload() {
    await tracker.writeReport("sidebar-close.json", Date.now());
  },
});
