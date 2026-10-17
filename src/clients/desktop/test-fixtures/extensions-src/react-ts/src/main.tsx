import { createRoot } from "react-dom/client";
import { defineView } from "/__agw/sdk.js";
import { App } from "./app";

const container = document.getElementById("root");
if (!container) throw new Error("The #root element is missing.");
const root = createRoot(container);

defineView({
  onLoad(api) {
    window.api = api;
    root.render(<App api={api} />);
  },
  onUnload() {
    root.unmount();
  },
});
