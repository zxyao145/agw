import { defineView } from "@agw/extension-sdk";
import { createRoot, type Root } from "react-dom/client";

import { SettingsForm } from "./settings-form";
import "../shared/document-error.css";
import "./settings.css";

let root: Root | null = null;

defineView({
  onLoad(api) {
    api.theme.apply();
    const container = document.getElementById("root");
    if (!container) throw new Error("The #root element is missing.");
    root = createRoot(container);
    root.render(<SettingsForm api={api} />);
  },
  onUnload() {
    root?.unmount();
    root = null;
  },
});
