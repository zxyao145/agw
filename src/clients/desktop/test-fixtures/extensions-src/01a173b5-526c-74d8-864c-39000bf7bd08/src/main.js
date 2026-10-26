import { createApp } from "vue";
import { defineView } from "/__agw/sdk.js";
import App from "./App.vue";

let app = null;

defineView({
  onLoad(api) {
    window.api = api;
    app = createApp(App, { api });
    app.mount("#app");
  },
  onUnload() {
    app.unmount();
  },
});
