// 命令 spin 让 background 进入无限循环。The spin command puts the background into an infinite loop.
import { defineBackground } from "/__agw/sdk.js";

defineBackground({
  activate(api) {
    window.api = api;
  },

  commands: {
    spin() {
      for (;;) {
        // 不再回复心跳。Never answers the heartbeat again.
      }
    },
  },
});
