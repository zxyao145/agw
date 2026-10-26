// deactivate 返回一个不会完成的 Promise。The deactivate hook returns a promise that never settles.
import { defineBackground } from "/__agw/sdk.js";

defineBackground({
  activate(api) {
    window.api = api;
  },

  deactivate() {
    return new Promise(() => undefined);
  },
});
