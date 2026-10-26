// background 正常激活。The background activates normally.
import { defineBackground } from "/__agw/sdk.js";

defineBackground({
  activate(api) {
    window.api = api;
  },
});
