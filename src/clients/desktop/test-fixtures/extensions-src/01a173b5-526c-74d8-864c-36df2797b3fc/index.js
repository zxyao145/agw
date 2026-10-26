// 没有任何权限的主区视图，测试通过 window.api 发起被拒绝的访问。
// Main view without permissions; the test drives rejected accesses through window.api.
import { defineView } from "/__agw/sdk.js";

defineView({
  onLoad(api) {
    window.api = api;
  },
});
