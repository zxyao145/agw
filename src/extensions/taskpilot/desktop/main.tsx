import { defineView, type ExtensionApi } from "@agw/extension-sdk";
import { createRoot, type Root } from "react-dom/client";

import { TaskPilotApp } from "./taskpilot-app";
import "./taskpilot.css";

let extension: ExtensionApi | null = null;
let root: Root | null = null;

function render(): void {
  if (!extension) return;
  root ??= createRoot(document.getElementById("root")!);
  root.render(<TaskPilotApp extension={extension} />);
}

defineView({
  onLoad(loaded) {
    extension = loaded;
    extension.theme.apply();
    render();
  },
  onShow: render,
  onUnload() {
    root?.unmount();
    root = null;
    extension = null;
  },
});
