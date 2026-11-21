import { contextBridge, ipcRenderer } from "electron";

import type { AgwFloatingChatButtonBridge } from "../shared/contracts";

const bridge: AgwFloatingChatButtonBridge = {
  getState: () => ipcRenderer.invoke("agw:floating-chat-button:get-state"),
  ready: () => ipcRenderer.send("agw:floating-chat-button:ready"),
  drag: (drag) => ipcRenderer.send("agw:floating-chat-button:drag", drag),
  open: () => ipcRenderer.send("agw:floating-chat-button:open"),
  onState: (listener) => {
    const handler = (_event: Electron.IpcRendererEvent, state: Parameters<typeof listener>[0]) =>
      listener(state);
    ipcRenderer.on("agw:floating-chat-button:state", handler);
    return () => ipcRenderer.off("agw:floating-chat-button:state", handler);
  },
};

contextBridge.exposeInMainWorld("agwFloatingChatButton", bridge);
