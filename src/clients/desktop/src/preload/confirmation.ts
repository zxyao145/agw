// 确认窗口的 preload：只提供读取请求、报告绘制完成与回答三个调用。
// The confirmation window preload: exposes only reading the request, reporting the render and answering.
import { contextBridge, ipcRenderer } from "electron";

import type { AgwConfirmationBridge } from "../shared/contracts";

const bridge: AgwConfirmationBridge = {
  request: () => ipcRenderer.invoke("agw-confirmation:request"),
  ready: (requestId, height) => ipcRenderer.send("agw-confirmation:ready", requestId, height),
  answer: (requestId, answer) => {
    ipcRenderer
      .invoke("agw-confirmation:answer", requestId, answer)
      .catch((error: unknown) => console.error(error));
  },
};

contextBridge.exposeInMainWorld("agwConfirmation", bridge);
