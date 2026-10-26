// sample 的主区视图：记录钩子参数与页面实例，跟踪执行会话，统计文件监听事件。
// Main view of the sample fixture: records hook parameters and the page instance, tracks sessions and counts watch events.
import { defineView } from "/__agw/sdk.js";
import { createSessionTracker } from "./shared.js";

let tracker = null;
window.watchCount = 0;
window.watchEvents = [];

defineView({
  onLoad(api, params) {
    window.api = api;
    window.loadCount = (window.loadCount ?? 0) + 1;
    window.lastParams = params;
    window.instance ??= crypto.randomUUID();
    tracker = createSessionTracker(api);
    window.openTrackedSession = tracker.open;
    window.startWatching = async (uri) => {
      await api.fs.watch(uri, {}, (event) => {
        window.watchCount += 1;
        window.watchEvents.push(event);
      });
    };
  },

  onShow(params) {
    window.lastShowParams = params;
  },

  async onUnload() {
    await tracker.writeReport("main-close.json", Date.now());
  },
});
