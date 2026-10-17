// 没有 background 的 extension 的主区视图。Main view of an extension without a background.
import { defineView } from "/__agw/sdk.js";

defineView({
  onLoad(api) {
    window.api = api;
  },
});
