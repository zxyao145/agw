// activate 等待本地 HTTP 服务中永远不响应的路径。The activate hook waits for a path of the local HTTP service that never responds.
import { defineBackground } from "/__agw/sdk.js";

defineBackground({
  async activate(api) {
    window.api = api;
    // 服务地址取自 manifest，端口占位符只出现在 manifest.json 中。The origin comes from the manifest, the only file with the port placeholder.
    await fetch(`${api.manifest.hostPermissions[0]}/never`);
  },
});
