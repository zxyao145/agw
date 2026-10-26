// sample 的设置页。Settings page of the sample fixture.
import { defineView } from "/__agw/sdk.js";

defineView({
  onLoad(api) {
    window.api = api;
    document.body.textContent = "Sample settings";
  },
});
