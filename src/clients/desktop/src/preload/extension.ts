// extension frame 的 preload：不暴露任何方法，只转交握手消息与端口，并报告被 CSP 拦截的请求。
// The extension frame preload exposes nothing; it relays the handshake and the port and reports CSP-blocked requests.
import { ipcRenderer } from "electron";

window.addEventListener("message", (event: MessageEvent) => {
  if (event.source !== window) return;
  const data = event.data as { type?: unknown; manifestVersion?: unknown } | null;
  if (typeof data !== "object" || data === null || data.type !== "agw-extension/hello") return;
  ipcRenderer.send("agw-extension:hello", { manifestVersion: data.manifestVersion });
});

ipcRenderer.on("agw-extension:port", (event) => {
  window.postMessage({ type: "agw-extension/port" }, location.origin, event.ports);
});

// CSP 在请求发出前拒绝，主进程看不到这些请求；转交给主进程记录为被拦截的请求。
// CSP refuses requests before they are sent, so main never sees them; forward them as blocked requests.
document.addEventListener("securitypolicyviolation", (event) => {
  ipcRenderer.send("agw-extension:csp-violation", {
    blockedUri: event.blockedURI,
    directive: event.effectiveDirective,
  });
});
