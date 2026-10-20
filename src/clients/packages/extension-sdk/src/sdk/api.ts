import type {
  DirectoryEntry,
  Disposable,
  ExecutionCloseReason,
  ExecutionMessage,
  ExecutionSessionApi,
  ExtensionApi,
  ExtensionWelcome,
  FileChangeEvent,
  FileKind,
  FileStat,
  HostContext,
  HttpResponse,
  TextDocument,
  ThemeInfo,
} from "../api";
import { createExtensionError, type RpcEndpoint } from "../rpc";
import { openBinaryDocument, openTextDocument } from "./documents";

export type EventRouter = Map<string, Set<(data: unknown) => void>>;

function subscribe(
  router: EventRouter,
  name: string,
  listener: (data: unknown) => void,
): Disposable {
  let listeners = router.get(name);
  if (!listeners) {
    listeners = new Set();
    router.set(name, listeners);
  }
  listeners.add(listener);
  return { dispose: () => listeners.delete(listener) };
}

function fileKind(uri: string): FileKind {
  if (uri.startsWith("file:")) return "local";
  if (uri.startsWith("agw-cloud:")) return "cloud";
  throw createExtensionError("ERR_INVALID_URI", `Unsupported URI: ${uri}`);
}

function decodeText(data: Uint8Array): string {
  return new TextDecoder("utf-8", { ignoreBOM: false }).decode(data);
}

/** 把主题变量与 dark class 写到 document 上。Applies theme variables and the dark class to the document. */
function applyTheme(theme: ThemeInfo): void {
  const root = document.documentElement;
  for (const [name, value] of Object.entries(theme.tokens)) root.style.setProperty(name, value);
  root.classList.toggle("dark", theme.kind === "dark");
  root.style.colorScheme = theme.kind;
}

export function createExtensionApi(
  endpoint: RpcEndpoint,
  welcome: ExtensionWelcome,
  router: EventRouter,
): ExtensionApi {
  let context: HostContext = welcome.context;
  let theme: ThemeInfo = welcome.theme;
  subscribe(router, "context.changed", (data) => (context = data as HostContext));
  subscribe(router, "theme.changed", (data) => (theme = data as ThemeInfo));

  const call = <T>(method: string, params: unknown = {}, signal?: AbortSignal) =>
    endpoint.request<T>(method, params, { signal });

  const createSession = (sessionId: string, conversationId: string): ExecutionSessionApi => ({
    conversationId,
    send: (input) => call("execution.send", { sessionId, ...input }),
    interrupt: () => call("execution.interrupt", { sessionId }),
    respond: (response) => call("execution.respond", { sessionId, response }),
    setPermissionMode: (mode) => call("execution.setPermissionMode", { sessionId, mode }),
    onMessage: (listener) =>
      subscribe(router, "execution.message", (data) => {
        const event = data as { sessionId: string; message: ExecutionMessage };
        if (event.sessionId === sessionId) listener(event.message);
      }),
    onClose: (listener) =>
      subscribe(router, "execution.closed", (data) => {
        const event = data as { sessionId: string; reason: ExecutionCloseReason };
        if (event.sessionId === sessionId) listener(event.reason);
      }),
    dispose: () => call("execution.dispose", { sessionId }),
  });

  const api: ExtensionApi = {
    manifest: welcome.manifest,
    context: {
      get: () => context,
      onDidChange: (listener) =>
        subscribe(router, "context.changed", (data) => listener(data as HostContext)),
      listProjects: () => call("context.listProjects"),
      listTargets: () => call("context.listTargets"),
    },
    workspace: {
      showSidebarView: (id) => call("workspace.showSidebarView", { id }),
      toggleSidebarView: (id) => call("workspace.toggleSidebarView", { id }),
      setSidebarVisible: (visible) => call("workspace.setSidebarVisible", { visible }),
      openMainView: (id, params) => call("workspace.openMainView", { id, params: params ?? {} }),
      openFile: (uri) => call("workspace.openFile", { uri }),
      setUnsavedChanges: (unsaved) => call("workspace.setUnsavedChanges", { unsaved }),
    },
    fs: {
      kind: fileKind,
      readFile: (uri, options) => call<Uint8Array>("fs.readFile", { uri }, options?.signal),
      readTextFile: async (uri, options) =>
        decodeText(await call<Uint8Array>("fs.readFile", { uri }, options?.signal)),
      writeFile: (uri, data, options) =>
        call(
          "fs.writeFile",
          {
            uri,
            data: typeof data === "string" ? new TextEncoder().encode(data) : data,
            overwrite: options?.overwrite ?? true,
            createParents: options?.createParents ?? true,
            expectedSha256: options?.expectedSha256,
          },
          options?.signal,
        ),
      readTextDocument: (uri, options) =>
        call<TextDocument>(
          "fs.readTextDocument",
          { uri, encoding: options?.encoding },
          options?.signal,
        ),
      writeTextDocument: (uri, text, options) =>
        call<{ sha256: string }>(
          "fs.writeTextDocument",
          {
            uri,
            text,
            encoding: options.encoding,
            bom: options.bom,
            eol: options.eol,
            expectedSha256: options.expectedSha256,
            createParents: options.createParents ?? true,
          },
          options.signal,
        ),
      listTextEncodings: () => call("fs.listTextEncodings"),
      readDirectory: (uri) => call<DirectoryEntry[]>("fs.readDirectory", { uri }),
      stat: (uri) => call<FileStat>("fs.stat", { uri }),
      exists: (uri) => call<boolean>("fs.exists", { uri }),
      createDirectory: (uri, options) =>
        call("fs.createDirectory", { uri, recursive: options?.recursive ?? false }),
      delete: (uri, options) => call("fs.delete", { uri, recursive: options?.recursive ?? false }),
      rename: (from, to, options) =>
        call("fs.rename", { from, to, overwrite: options?.overwrite ?? false }),
      watch: async (uri, options, listener) => {
        const { watchId } = await call<{ watchId: string }>("fs.watch", {
          uri,
          recursive: options.recursive ?? false,
        });
        const subscription = subscribe(router, "fs.changed", (data) => {
          const event = data as { watchId: string; event: FileChangeEvent };
          if (event.watchId === watchId) listener(event.event);
        });
        return {
          dispose: () => {
            subscription.dispose();
            void call("fs.unwatch", { watchId }).catch(() => undefined);
          },
        };
      },
    },
    dialog: {
      showOpenDialog: (options) => call("dialog.showOpenDialog", options),
      showSaveDialog: (options) => call("dialog.showSaveDialog", options),
      showMessageBox: (options) => call("dialog.showMessageBox", options),
    },
    documents: {
      openText: (options) => openTextDocument(api, options),
      openBinary: (options) => openBinaryDocument(api, options),
    },
    shell: {
      revealInFileManager: (uri) => call("shell.revealInFileManager", { uri }),
    },
    execution: {
      createSession: async (options) => {
        const { sessionId, conversationId } = await call<{
          sessionId: string;
          conversationId: string;
        }>("execution.createSession", options);
        return createSession(sessionId, conversationId);
      },
    },
    a2a: {
      listAgents: (options) => call("a2a.listAgents", {}, options?.signal),
      getAgentCard: (agentName, options) =>
        call("a2a.getAgentCard", { agentName }, options?.signal),
      request: (agentName, request, options) =>
        call(
          "a2a.request",
          { agentName, method: request.method, params: request.params },
          options?.signal,
        ),
      stream: async (agentName, request, listener, options) => {
        const streamId = crypto.randomUUID();
        const subscription = subscribe(router, "a2a.event", (data) => {
          const event = data as { streamId: string; event: unknown };
          if (event.streamId === streamId) listener(event.event);
        });
        try {
          await call(
            "a2a.stream",
            { streamId, agentName, method: request.method, params: request.params },
            options?.signal,
          );
        } finally {
          subscription.dispose();
        }
      },
    },
    http: {
      request: async (request) => {
        const { signal, ...rest } = request;
        const response = await call<Omit<HttpResponse, "text" | "json">>(
          "http.request",
          rest,
          signal,
        );
        return {
          ...response,
          text: () => decodeText(response.body),
          json: <T>() => JSON.parse(decodeText(response.body)) as T,
        };
      },
    },
    chat: {
      composer: { insertText: (text) => call("chat.composer.insertText", { text }) },
    },
    ui: {
      notify: (notification) => {
        void call("ui.notify", notification).catch((error: unknown) => console.error(error));
      },
      openExternal: (url) => call("ui.openExternal", { url }),
    },
    theme: {
      get: () => theme,
      onDidChange: (listener) =>
        subscribe(router, "theme.changed", (data) => listener(data as ThemeInfo)),
      apply: () => {
        applyTheme(theme);
        return subscribe(router, "theme.changed", (data) => applyTheme(data as ThemeInfo));
      },
    },
  };
  return api;
}
