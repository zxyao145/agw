// activate 抛出异常，激活失败。The activate hook throws, so activation fails.
import { defineBackground } from "/__agw/sdk.js";

defineBackground({
  activate(api) {
    window.api = api;
    throw new Error("broken on purpose");
  },
});
