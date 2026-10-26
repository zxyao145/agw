// 主区视图在 onUnload 中把时间写入存储目录的 view-unload.json。The main view writes the time to view-unload.json in storage on unload.
import { defineView } from "/__agw/sdk.js";

let connectedApi = null;

defineView({
  onLoad(api) {
    window.api = api;
    connectedApi = api;
  },

  async onUnload() {
    const { storageUri } = connectedApi.context.get().extension;
    await connectedApi.fs.writeFile(`${storageUri}/view-unload.json`, JSON.stringify({ at: Date.now() }));
  },
});
