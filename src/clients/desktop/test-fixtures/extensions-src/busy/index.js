// 点击 #spin 后该视图进入无限循环。Clicking #spin puts this view into an infinite loop.
import { defineView } from "/__agw/sdk.js";

document.getElementById("spin").addEventListener("click", () => {
  for (;;) {
    // 不再回复心跳。Never answers the heartbeat again.
  }
});

defineView({
  onLoad(api) {
    window.api = api;
  },
});
