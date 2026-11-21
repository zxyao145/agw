import { mountFloatingChatButton } from "@agw/extension-sdk";
import type {
  AgwFloatingChatButtonBridge,
  FloatingChatButtonDrag,
  FloatingChatButtonViewState,
} from "../shared/contracts";

declare global {
  interface Window {
    agwFloatingChatButton: AgwFloatingChatButtonBridge;
  }
}

const bridge = window.agwFloatingChatButton;

function renderState(state: FloatingChatButtonViewState): void {
  document.body.style.setProperty("--popover", state.background);
  document.body.style.setProperty("--popover-foreground", state.foreground);
  document.body.style.setProperty("--border", state.border);
  document.body.style.setProperty("--floating-chat-x", `${state.dragging ? state.x : 0}px`);
  document.body.style.setProperty("--floating-chat-y", `${state.dragging ? state.y : 0}px`);
}

mountFloatingChatButton(document.body, {
  onOpen: () => bridge.open(),
  onDrag: (drag: FloatingChatButtonDrag) => bridge.drag(drag),
});
bridge.onState(renderState);

async function initialize(): Promise<void> {
  renderState(await bridge.getState());
  bridge.ready();
}

void initialize();
