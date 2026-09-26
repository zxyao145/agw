import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { createRequire } from "node:module";
import test from "node:test";
import { runInNewContext } from "node:vm";
import { JSDOM } from "jsdom";
import * as React from "react";
import { createRoot } from "react-dom/client";
import { transformSync } from "esbuild";

import { buildChatHref } from "../../../lib/chat-route";
import * as sessionRouting from "./lib/session-routing";
import type { ChatProps } from "../../components/message/chat";
import type { ChatWorkspaceProps } from "./chat-workspace";
import { ExecutionReconnectingDialog } from "../../components/message/execution-reconnecting-dialog";
import { QueryClient, QueryClientProvider, useQueryClient } from "@agw/components/query";
import * as chatRuntime from "@agw/chat-runtime";
import type { ExecutionHubHandlers, ExecutionRequest, ExecutionSetting } from "@agw/chat-runtime";

async function checkConversationSession(kind: string, strictMode = false) {
  const failHistory = kind === "restore-failure";
  let hasAdditionalDirectory = kind === "directories";
  const fileReads: unknown[][] = [];
  const conversation = {
    conversationId: "conversation-1",
    contextId: "original-context",
    usage: { inputTokens: 1, outputTokens: 1, totalTokens: 2 },
    resumeState: { targetType: 0, targetId: "agent-1" },
  };
  const messages: import("@agw/api").AiMessage[] = [
    { messageId: "message-1", role: "user", contents: [] },
  ];
  if (kind === "reasoning-history") {
    messages[0].contents = [{ type: "TextContent", content: "Fix the issue" }];
    messages.push(
      ...["The", " user", " wants", " me", " to", " fix"].map((content) => ({
        messageId: "thinking",
        role: "assistant",
        author: "claude-code",
        additionalProperties: { modelName: "deepseek-v4-pro" },
        contents: [{ type: "TextReasoningContent", content }],
      })),
      {
        messageId: "result",
        role: "assistant",
        additionalProperties: { type: "result" },
        contents: [{ type: "TextContent", content: "Done" }],
      },
    );
  }
  if (kind === "result-schema") {
    messages[0]!.contents = [
      {
        type: "TextContent",
        content: "Review",
        additionalProperties: { targetType: "agent", targetId: "agent-1" },
      },
    ];
    messages.push({
      messageId: "result-1",
      role: "assistant",
      author: "codex",
      additionalProperties: { type: "result" },
      contents: [{ type: "TextContent", content: '{"approved":false}' }],
    });
  }
  const dom = new JSDOM("<div id='root'></div>", {
    url: "http://localhost/desktop/chat/?projectId=project-1&conversationId=conversation-1",
  });
  const refreshTimers = new Map<number, () => void>();
  if (kind === "history-refresh") {
    Object.defineProperty(dom.window.document, "visibilityState", {
      configurable: true,
      value: "visible",
    });
    dom.window.setInterval = ((callback: () => void) => {
      refreshTimers.set(1, callback);
      return 1;
    }) as typeof dom.window.setInterval;
    dom.window.clearInterval = (id) => {
      refreshTimers.delete(id!);
    };
  }
  const originalWindow = Object.getOwnPropertyDescriptor(globalThis, "window");
  Object.defineProperty(globalThis, "window", { configurable: true, value: dom.window });
  const actHost = globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT?: boolean };
  const originalActEnvironment = actHost.IS_REACT_ACT_ENVIRONMENT;
  actHost.IS_REACT_ACT_ENVIRONMENT = true;
  dom.window.matchMedia = () =>
    ({
      matches: false,
      addEventListener() {},
      removeEventListener() {},
    }) as unknown as MediaQueryList;

  let finishRecovery!: () => void;
  const recoveryReady = new Promise<void>((resolve) => {
    finishRecovery = resolve;
  });
  let reconnectHandlers: ExecutionHubHandlers | undefined;
  let managedStatus = kind === "restore-active" ? "running" : "idle";
  let activeTurnSnapshot: {
    streamingScopeId: string;
    messages: import("@agw/api").AiMessage[];
  } | null = null;
  let currentReconnectState:
    | import("@agw/chat-runtime/execution-session").ExecutionReconnectState
    | null = null;
  let detailsRequests = 0;
  let messageRequests = 0;
  const observed: {
    explorer?: {
      directoryId?: string | null;
      onDirectoryChange: (value: string | null) => void;
      onFileSelected: (path: string) => void;
    };
    file?: { selectedFile: string | null };
    chat?: ChatProps;
    items?: import("@agw/chat-core").ConversationRenderItem[];
    input?: {
      onClearSession: () => void;
      isTransitioning: boolean;
      isExecuting: boolean;
      onExecute: (text: string, attachments: []) => void;
      topLeft?: React.ReactNode;
    };
    newChat?: () => void;
    refreshSignal?: number;
    conversationStatuses?: ReadonlyMap<string, string>;
    currentConversationTurnId?: string | null;
    selectAgent?: (selection: { agentType: number; agentId: string }) => void;
    selectTab?: (value: string) => void;
  } = {};
  const attachedContexts = new Set<string>();
  const conversationStatuses = new chatRuntime.ConversationStatusStore();
  const supersededListeners = new Set<(key: { serverId: string; projectId: string }) => void>();
  let activityRequests = 0;
  const executions: (ExecutionRequest & { contextId: string })[] = [];
  const configurations: ExecutionSetting[] = [];
  let finishHistory!: () => void;
  const historyReady = new Promise<void>((resolve) => {
    finishHistory = resolve;
  });
  const errors: unknown[] = [];
  let searchParams = new URLSearchParams(dom.window.location.search);
  const router = {
    replace(href: string) {
      dom.window.history.replaceState(null, "", href);
    },
  };
  const pending = new Map<
    string,
    { resolve: (v: unknown) => void; reject: (e: Error) => void; promise: Promise<unknown> }
  >();
  for (const id of ["conversation-2", "conversation-3"]) {
    let resolve!: (v: unknown) => void, reject!: (e: Error) => void;
    const promise = new Promise((a, b) => {
      resolve = a;
      reject = b;
    });
    pending.set(id, { resolve, reject, promise });
  }
  const queryData: Record<string, unknown[]> = {
    projects: [
      { id: "project-1", name: "Project" },
      { id: "project-2", name: "Second" },
    ],
    agents: [
      {
        id: "agent-1",
        name: "first",
        displayName: "First",
        enable: true,
        resultFormat: kind === "result-schema" ? "json" : "markdown",
      },
      { id: "agent-2", name: "second", displayName: "Second", enable: true },
    ],
    agentflows: [],
  };
  function Container({ children }: { children?: React.ReactNode }) {
    return React.createElement(React.Fragment, null, children);
  }
  const splitLayout = Object.assign(Container, { Left: Container, Right: Container });
  const components = new Proxy(
    {
      cn: () => "",
      Drawer: () => null,
      Tabs: ({
        children,
        inert,
        "aria-hidden": ariaHidden,
        onValueChange,
      }: React.HTMLAttributes<HTMLDivElement> & {
        onValueChange?: (value: string) => void;
      }) => {
        // The sidebar renders the conversation list or the explorer by active tab,
        // so expose the switch to let tests pick one.
        observed.selectTab = onValueChange;
        return React.createElement("div", { inert, "aria-hidden": ariaHidden }, children);
      },
    },
    { get: (target, key) => Reflect.get(target, key) ?? Container },
  );
  const modules: Record<string, unknown> = {
    "@agw/components": components,
    "@agw/components/query": {
      useQueryClient,
      useQuery: ({ queryKey }: { queryKey: string[] }) => ({
        data:
          queryKey[0] === "execution-permissions"
            ? { supportedPermissionModes: ["fullAccess", "alwaysAsk", "allowSameArguments"] }
            : queryData[queryKey[0]],
      }),
    },
    "next/navigation": { useRouter: () => router, useSearchParams: () => searchParams },
    sonner: { toast: { error: (error: unknown) => errors.push(error) } },
    "@agw/projects": {
      getProjectDirectories: () => [
        { id: null, name: "primary", path: "/primary", isPrimary: true },
        ...(hasAdditionalDirectory
          ? [{ id: "extra", name: "extra", path: "/extra", isPrimary: false }]
          : []),
      ],
      Explorer: (props: NonNullable<typeof observed.explorer>) => {
        observed.explorer = props;
        return null;
      },
      FileContent: (props: NonNullable<typeof observed.file>) => {
        observed.file = props;
        return null;
      },
      readFile: async (...args: unknown[]) => {
        fileReads.push(args);
        return "contents";
      },
      getFileDiff: async (...args: unknown[]) => {
        fileReads.push(args);
        return { diff: "", unchanged: true };
      },
      clearProjectConversationRecords: async () => true,
      getConversationActivity: async () => {
        activityRequests += 1;
        return [];
      },
      getProjectConversationDetails: async (_project: string, id: string) => {
        if (id !== "conversation-1") return pending.get(id)!.promise;
        detailsRequests += 1;
        await historyReady;
        if (failHistory) throw new Error("History unavailable");
        return conversation;
      },
      getProjectConversationMessages: async (_project: string, id: string) => {
        if (id !== "conversation-1")
          return { items: [], hasMore: false, nextCursor: null, turns: [] };
        messageRequests += 1;
        await historyReady;
        return { items: messages, nextCursor: null, hasMore: false, turns: [] };
      },
      // A cached sidebar publishes its summary in the child's effect, before the
      // workspace's route hydration effect. A summary is not a hydrated session.
      ConversationList: (props: {
        refreshSignal?: number;
        conversationStatuses?: ReadonlyMap<string, string>;
        currentConversationTurnId?: string | null;
        onNewConversation?: () => void;
        onActiveConversationResolved?: (value: unknown) => void;
      }) => {
        observed.refreshSignal = props.refreshSignal;
        observed.conversationStatuses = props.conversationStatuses;
        observed.currentConversationTurnId = props.currentConversationTurnId;
        observed.newChat = props.onNewConversation;
        React.useEffect(() => {
          props.onActiveConversationResolved?.(conversation);
        }, [props.onActiveConversationResolved]);
        return null;
      },
    },
    "../../components/agent-selector": {
      AgentSelector: (props: { onSelect: typeof observed.selectAgent }) => {
        observed.selectAgent = props.onSelect;
        return null;
      },
    },
    "../../components/message/execution-reconnecting-dialog": { ExecutionReconnectingDialog },
    "../../../lib/chat-route": { buildChatHref },
    "./settings-storage": {
      chatSettingsStorage: {
        get: () => (kind === "result-only" ? { resultOnly: true } : {}),
        set() {},
      },
    },
    "./components/split-layout": { default: splitLayout, __esModule: true },
    "./lib/chat-settings": {},
    "./lib/session-routing": sessionRouting,
    "@agw/chat-runtime": {
      ...chatRuntime,
      executionSessionManager: {
        conversationStatuses,
        subscribeReconnected: () => () => undefined,
        subscribeSupersededTurn: (
          listener: (key: { serverId: string; projectId: string }) => void,
        ) => {
          supersededListeners.add(listener);
          return () => supersededListeners.delete(listener);
        },
        has: (key: { contextId: string }) =>
          (kind === "restore-active" || kind.startsWith("work-close")) &&
          attachedContexts.has(key.contextId),
        attach: (key: { contextId: string }, handlers: ExecutionHubHandlers) => {
          reconnectHandlers = handlers;
          attachedContexts.add(key.contextId);
          return {
            matchesKey: (candidate: typeof key) => candidate.contextId === key.contextId,
            detach() {},
            interruptAndWait: async () => {},
            dispose: async () => {},
            getStatus: () => managedStatus,
            listAgentflowCheckpoints: async () => [],
            getReconnectState: () => currentReconnectState,
            getActiveTurnSnapshot: () => activeTurnSnapshot,
            configure: async (setting: ExecutionSetting) => {
              configurations.push(setting);
              if (kind === "restore-active") await recoveryReady;
              return { restoredDurableExecution: false };
            },
            execute: async (request: ExecutionRequest) => {
              if (kind.startsWith("work-close")) {
                managedStatus = "running";
                activeTurnSnapshot = {
                  streamingScopeId: request.input.messageId,
                  messages: [{ ...request.input, role: "user" }],
                };
              }
              executions.push({ ...request, contextId: key.contextId });
            },
          };
        },
      },
    },
    "../../execution-platform": { useExecutionPlatform: () => ({ serverId: "local" }) },
  };
  async function loadComponent<Props extends object>(
    url: URL,
    overrides: Record<string, unknown> = {},
  ) {
    const source = await readFile(url, "utf8");
    const compiled = transformSync(source, {
      loader: "tsx",
      format: "cjs",
      jsx: "automatic",
      target: "es2022",
    }).code;
    const moduleRef: { exports: Record<string, React.ComponentType<Props>> } = { exports: {} };
    const require = createRequire(url);
    const dependencies = { ...modules, ...overrides };
    runInNewContext(compiled, {
      module: moduleRef,
      exports: moduleRef.exports,
      console,
      ResizeObserver: class {
        observe() {}
        disconnect() {}
      },
      require: (name: string) => (name in dependencies ? dependencies[name] : require(name)),
      window: dom.window,
      URLSearchParams,
      AbortController,
      DOMException,
      requestAnimationFrame: (callback: () => void) => setTimeout(callback, 0),
      cancelAnimationFrame: clearTimeout,
    });
    return moduleRef.exports;
  }
  const { Chat } = await loadComponent<ChatProps>(
    new URL("../../components/message/chat.tsx", import.meta.url),
    {
      "./chat-input": {
        ChatInput: (props: NonNullable<typeof observed.input>) => {
          observed.input = props;
          // The workspace passes the agent selector through the input's top-left slot,
          // so mount it here to keep observing target changes.
          return React.createElement(React.Fragment, null, props.topLeft ?? null);
        },
      },
      "./chat-aside": { ChatAside: () => null },
      "./conversation": {
        Conversation: ({ items }: { items: NonNullable<typeof observed.items> }) => {
          observed.items = items;
          return null;
        },
      },
    },
  );
  modules["../../components/message/chat"] = {
    Chat: (props: ChatProps) => {
      observed.chat = props;
      return React.createElement(Chat, props);
    },
  };
  modules["../../conversation-statuses"] = await loadComponent(
    new URL("../../conversation-statuses.ts", import.meta.url),
  );
  const { ChatWorkspace } = await loadComponent<ChatWorkspaceProps>(
    new URL("./chat-workspace.tsx", import.meta.url),
  );
  const root = createRoot(dom.window.document.getElementById("root")!);
  const queryClient = new QueryClient();
  const renderWithQueries = (children: React.ReactNode) =>
    root.render(
      React.createElement(
        strictMode ? React.StrictMode : React.Fragment,
        null,
        React.createElement(QueryClientProvider, { client: queryClient }, children),
      ),
    );
  try {
    const renderWorkspace = () =>
      renderWithQueries(
        React.createElement(ChatWorkspace, {
          routeBasePath: "/desktop/chat",
          showProjectSelect: false,
        }),
      );
    if (kind.startsWith("drawer")) {
      const flow = kind === "drawer-agentflow";
      const drawerPath = flow
        ? "../../../../../agents/src/ui-web/pages/agentflows/components/execute-agentflow-drawer.tsx"
        : "../../../../../agents/src/ui-web/pages/agents/components/execute-agent-drawer.tsx";
      // Keep children mounted during the close animation, as Vaul/Radix Presence does.
      const drawerModules = await loadComponent<Record<string, unknown>>(
        new URL(drawerPath, import.meta.url),
        {
          "@agw/chat": modules["../../components/message/chat"],
          react: React,
          "lucide-react": { X: () => null },
          "@agw/components": new Proxy(
            { cn: () => "" },
            { get: (target, key) => Reflect.get(target, key) ?? Container },
          ),
        },
      );
      const Drawer = drawerModules[flow ? "ExecuteAgentflowDrawer" : "ExecuteAgentDrawer"];
      const agent = { id: "agent-1", name: "First", resultFormat: "json" };
      const draw = (open: boolean) =>
        renderWithQueries(
          React.createElement(
            Drawer,
            flow
              ? { open, onOpenChange() {}, agentflow: agent }
              : { open, setOpen() {}, executingAgent: agent },
          ),
        );
      await React.act(async () => draw(true));
      await React.act(async () => observed.input!.onExecute("first turn", []));
      assert.equal(executions.length, 1);
      if (!flow) {
        await React.act(async () =>
          reconnectHandlers!.onMessage({
            messageId: "drawer-result",
            role: "assistant",
            author: "claude-code",
            additionalProperties: { type: "result" },
            contents: [{ type: "TextContent", content: '{"approved":false}' }],
          }),
        );
        await React.act(async () =>
          reconnectHandlers!.onMessage({
            messageId: "drawer-finished",
            role: "system",
            additionalProperties: { type: "agw-turn-finished", status: "completed" },
            contents: [],
          }),
        );
        const result = observed.items?.find((item) => item.type === "result");
        assert.equal(
          result?.type === "result" ? result.message.contents[0]?.type : undefined,
          "json",
        );
      }
      await React.act(async () => draw(false));
      await React.act(async () => draw(true));
      assert.equal(observed.input?.isTransitioning, false);
      await React.act(async () => observed.input!.onExecute("second turn", []));
      assert.equal(executions.length, 2);
      assert.notEqual(executions[1].conversationId, executions[0].conversationId);
      assert.notEqual(
        executions[1].contextId,
        executions[0].contextId,
        "a new drawer session gets a new context",
      );
    } else {
      await React.act(async () => renderWorkspace());
      assert.equal(observed.input?.isTransitioning, true);
      await React.act(async () => observed.input!.onExecute("too early", []));
      assert.equal(executions.length, 0);
      await React.act(async () => finishHistory());
      if (kind === "reasoning-history") {
        const summary = observed.items?.find((item) => item.type === "work-summary");
        assert.ok(summary?.type === "work-summary");
        assert.equal(summary.items.length, 1);
        const item = summary.items[0];
        assert.equal(item.type, "message");
        assert.ok(item.type === "message");
        assert.deepEqual(item.message.contents, [
          {
            type: "reasoning",
            markdown: "The user wants me to fix",
            preview: "The user wants me to fix",
          },
        ]);
        return;
      }
      if (kind.startsWith("work-close")) {
        await React.act(async () => observed.input!.onExecute("Review changes", []));
        const initialHandlers = reconnectHandlers;
        if (kind === "work-close-cached") {
          await React.act(async () => observed.selectAgent!({ agentType: 0, agentId: "agent-2" }));
          assert.notEqual(
            reconnectHandlers,
            initialHandlers,
            "exercise the cached-client attachment",
          );
        }
        const handlers = reconnectHandlers!;
        await React.act(async () => {
          const updates: import("@agw/api").AiMessage[] = [
            {
              messageId: "process",
              role: "assistant",
              contents: [{ type: "TextContent", content: "Reviewing" }],
            },
            {
              messageId: "result",
              role: "assistant",
              additionalProperties: { type: "result" },
              contents: [{ type: "TextContent", content: "Done" }],
            },
          ];
          activeTurnSnapshot!.messages.push(...updates);
          for (const message of updates) handlers.onMessage(message);
          // A mode control update flushes the queued output without finishing the turn.
          handlers.onMessage({
            messageId: "mode",
            role: "system",
            additionalProperties: { type: "mode-status", mode: "execute" },
            contents: [],
          });
        });
        const summaryCount = () =>
          observed.items!.filter((item) => item.type === "work-summary").length;
        assert.ok(observed.items!.some((item) => item.type === "result"));
        assert.equal(summaryCount(), 0);
        await React.act(async () => handlers.onClose?.(new Error("Connection lost")));
        assert.equal(summaryCount(), 0, "disconnect alone must not hide the process");

        await React.act(async () =>
          observed.selectAgent!({
            agentType: 0,
            agentId: kind === "work-close-cached" ? "agent-1" : "agent-2",
          }),
        );
        assert.notEqual(reconnectHandlers, handlers, "reattach before accepting further callbacks");
        assert.equal(summaryCount(), 0);
        await React.act(async () => {
          if (kind === "work-close-cached") {
            managedStatus = "idle";
            reconnectHandlers!.onReconnected?.();
          } else {
            reconnectHandlers!.onMessage({
              messageId: "finished",
              role: "system",
              additionalProperties: { type: "agw-turn-finished", status: "completed" },
              contents: [],
            });
          }
        });
        assert.equal(
          summaryCount(),
          1,
          "fold only after terminal output or confirmed idle recovery",
        );
        return;
      }
      if (kind === "result-only") {
        assert.equal(observed.chat?.resultOnly, true);
        assert.equal(configurations.at(-1)?.resultOnly, true);
        return;
      }
      if (kind === "result-schema") {
        const resultContent = () => {
          const item = observed.items?.find((item) => item.type === "result");
          return item?.type === "result" ? item.message.contents[0] : undefined;
        };
        assert.deepEqual(resultContent(), { type: "json", text: '{\n  "approved": false\n}' });
        queryData.agents = [
          {
            id: "agent-1",
            name: "first",
            displayName: "First",
            enable: true,
            resultFormat: "markdown",
          },
        ];
        await React.act(async () => renderWorkspace());
        assert.equal(resultContent()?.type, "markdown");
        return;
      }
      if (kind === "reconnect" || kind === "reconnect-send") {
        assert.ok(reconnectHandlers);
        for (let retryAttempt = 1; retryAttempt <= 10; retryAttempt += 1) {
          await React.act(async () => {
            currentReconnectState = { status: "reconnecting", retryAttempt, retryDelayMs: 1_000 };
            reconnectHandlers!.onReconnecting?.(currentReconnectState);
          });
          assert.equal(
            dom.window.document.querySelectorAll("[inert]").length,
            retryAttempt <= 5 ? 0 : 3,
          );
          const dialog = dom.window.document.querySelector('[role="dialog"]');
          assert.equal(dialog !== null, retryAttempt > 5);
          if (dialog) assert.ok(dialog.textContent?.includes(`${retryAttempt - 5}/5`));
          assert.deepEqual(errors, []);
          if (retryAttempt === 5 && kind === "reconnect-send") {
            await React.act(async () =>
              observed.input!.onExecute("send while silently reconnecting", []),
            );
            assert.equal(executions.length, 1, "silent reconnection does not discard user input");
            return;
          }
          if (retryAttempt === 5) {
            await React.act(async () => reconnectHandlers!.onReconnected?.());
            assert.equal(dom.window.document.querySelector('[role="dialog"]'), null);
          }
        }
        await React.act(async () => reconnectHandlers!.onReconnected?.());
        assert.equal(dom.window.document.querySelector('[role="dialog"]'), null);
        assert.equal(dom.window.document.querySelector("[inert]"), null);
        return;
      }
      if (kind === "directories") {
        await React.act(async () => observed.selectTab!("files"));
        const contextId = observed.chat?.sessionSeed.contextId;
        assert.deepEqual(observed.chat?.searchDirectoryIds, [null, "extra"]);
        await React.act(async () => observed.explorer!.onFileSelected("README.md"));
        assert.equal(observed.file?.selectedFile, "README.md");
        await React.act(async () => observed.explorer!.onDirectoryChange("extra"));
        assert.equal(observed.file?.selectedFile, null);
        assert.equal(observed.chat?.directoryId, "extra");
        assert.equal(observed.chat?.sessionSeed.contextId, contextId);
        await React.act(async () => observed.explorer!.onFileSelected("README.md"));
        assert.equal(fileReads.at(-1)?.at(-1), "extra");
        hasAdditionalDirectory = false;
        queryData.projects = queryData.projects.map((project) => ({
          ...(project as Record<string, unknown>),
        }));
        await React.act(async () => renderWorkspace());
        assert.equal(observed.explorer?.directoryId, null);
        assert.deepEqual(observed.chat?.searchDirectoryIds, [null]);
        assert.equal(observed.file?.selectedFile, null);
        return;
      }
      if (kind === "restore-active") {
        assert.equal(
          observed.input?.isTransitioning,
          true,
          "history alone does not confirm execution is idle",
        );
        await React.act(async () => observed.input!.onExecute("too early after reload", []));
        assert.equal(executions.length, 0);
        await React.act(async () => finishRecovery());
        assert.equal(observed.input?.isTransitioning, false);
        assert.equal(observed.input?.isExecuting, true, "the old turn keeps the composer busy");
        await React.act(async () => observed.selectAgent!({ agentType: 0, agentId: "agent-2" }));
        assert.equal(
          observed.input?.isExecuting,
          true,
          "changing Agent cannot discard the conversation's active execution",
        );
        await React.act(async () => observed.input!.onExecute("duplicate", []));
        assert.equal(executions.length, 0);
        return;
      }
      if (kind === "history-refresh") {
        const statusScope = { serverId: "local", projectId: "project-1" };
        assert.equal(activityRequests, 1, "entering the page requests one status snapshot");
        assert.equal(observed.currentConversationTurnId, null);
        const before = observed.refreshSignal!;
        await React.act(async () => observed.input!.onExecute("long answer", []));
        // The stubbed session manager does not route messages, so write the turn start it would record.
        // 会话管理器由测试替换，不转发消息，这里写入它会记录的 Turn 开始事件。
        await React.act(async () =>
          conversationStatuses.turnStarted(statusScope, "conversation-1", "turn-1"),
        );
        assert.equal(refreshTimers.size, 0, "a running turn does not poll the conversation list");
        assert.equal(observed.refreshSignal, before + 1, "starting a turn refreshes history once");
        assert.equal(observed.conversationStatuses?.get("conversation-1"), "running");
        assert.equal(
          observed.currentConversationTurnId,
          "turn-1",
          "the turn ID lets history fetch the conversation a running turn creates",
        );
        await React.act(async () => {
          reconnectHandlers!.onMessage({
            messageId: "history-refresh-finished",
            role: "system",
            additionalProperties: { type: "agw-turn-finished", status: "completed" },
            contents: [],
          });
          conversationStatuses.turnFinished(statusScope, "conversation-1", "turn-1", "completed");
        });
        assert.equal(refreshTimers.size, 0, "ending a turn leaves no timer behind");
        assert.equal(observed.refreshSignal, before + 2, "ending a turn refreshes history once");
        assert.equal(observed.conversationStatuses?.get("conversation-1"), "idle");
        assert.equal(activityRequests, 1, "turn events update statuses without another snapshot");
        await React.act(async () => {
          for (const listener of supersededListeners)
            listener({ serverId: "local", projectId: "project-2" });
        });
        assert.equal(activityRequests, 1, "another project's superseded turn leaves this snapshot");
        await React.act(async () => {
          for (const listener of supersededListeners) listener(statusScope);
        });
        assert.equal(activityRequests, 2, "a superseded turn of this project fetches a snapshot");
        return;
      }
      if (kind === "restore" || kind === "restore-failure") {
        assert.equal(detailsRequests, strictMode ? 2 : 1);
        assert.equal(messageRequests, detailsRequests);
        await React.act(async () => observed.selectAgent!({ agentType: 0, agentId: "agent-2" }));
        await React.act(async () => observed.input!.onExecute("1+4=?", []));
        if (failHistory) {
          assert.equal(errors.length, 1);
          assert.equal(observed.input?.isTransitioning, true);
          assert.equal(executions.length, 0);
        } else {
          assert.deepEqual(errors, []);
          assert.equal(executions.length, 1);
          assert.equal(executions[0].conversationId, conversation.conversationId);
          assert.equal(executions[0].contextId, conversation.contextId);
          assert.equal(executions[0].agentId, "agent-2");
          assert.deepEqual(observed.chat?.sessionSeed.messages, messages);
          assert.equal(configurations.at(-1)?.contextId, conversation.contextId);
        }
        return;
      }
      assert.equal(observed.chat?.conversationId, "conversation-1");
      async function navigate(id: string) {
        dom.window.history.pushState(
          null,
          "",
          `/desktop/chat/?projectId=project-1&conversationId=${id}`,
        );
        searchParams = new URLSearchParams(dom.window.location.search);
        await React.act(async () => renderWorkspace());
      }
      if (["new-chat", "project-switch", "clear-history"].includes(kind)) {
        if (kind === "new-chat") {
          assert.ok(observed.newChat);
          await React.act(async () => observed.newChat!());
          searchParams = new URLSearchParams(dom.window.location.search);
          await React.act(async () => renderWorkspace());
        } else if (kind === "project-switch") {
          dom.window.history.pushState(null, "", "/desktop/chat/?projectId=project-2");
          searchParams = new URLSearchParams(dom.window.location.search);
          await React.act(async () => renderWorkspace());
        } else {
          const inputQueryKey = [
            "conversation-turn-inputs",
            "local",
            "conversation-1",
            observed.chat!.sessionSeed.revision,
          ];
          queryClient.setQueryData(inputQueryKey, [
            { inputMessageId: "message-1", inputSummary: "Previous input" },
          ]);
          await React.act(async () => observed.input!.onClearSession());
          assert.deepEqual(queryClient.getQueryData(inputQueryKey), []);
        }
        assert.equal(observed.input?.isTransitioning, false);
        await React.act(async () => observed.input!.onExecute("next turn", []));
        assert.equal(executions.length, 1);
        if (kind === "clear-history") {
          assert.equal(executions[0].conversationId, "conversation-1");
          assert.equal(executions[0].contextId, "original-context");
        } else {
          assert.notEqual(executions[0].conversationId, "conversation-1");
          assert.notEqual(executions[0].contextId, "original-context");
          assert.equal(
            observed.chat?.projectId,
            kind === "project-switch" ? "project-2" : "project-1",
          );
        }

        return;
      }
      await navigate("conversation-2");
      assert.equal(observed.input?.isTransitioning, true);
      if (kind === "route-failure") {
        await React.act(async () =>
          pending.get("conversation-2")!.reject(new Error("History unavailable")),
        );
        assert.equal(errors.length, 1);
        assert.equal(observed.input?.isTransitioning, true);
        await React.act(async () => observed.input!.onExecute("for conversation-2", []));
        assert.equal(
          executions.length,
          0,
          "failed navigation must not send to the old conversation",
        );
        assert.equal(
          new URLSearchParams(dom.window.location.search).get("conversationId"),
          "conversation-2",
        );
        await navigate("conversation-3");
        await React.act(async () =>
          pending
            .get("conversation-3")!
            .resolve({ ...conversation, conversationId: "conversation-3", contextId: "context-3" }),
        );
        assert.equal(observed.input?.isTransitioning, false);
        await React.act(async () => observed.input!.onExecute("recovered", []));
        assert.equal(executions.length, 1);
        assert.equal(executions[0].conversationId, "conversation-3");
        assert.equal(executions[0].contextId, "context-3");
      } else {
        await navigate("conversation-3");
        await React.act(async () =>
          pending
            .get("conversation-3")!
            .resolve({ ...conversation, conversationId: "conversation-3", contextId: "context-3" }),
        );
        await React.act(async () =>
          pending
            .get("conversation-2")!
            .resolve({ ...conversation, conversationId: "conversation-2", contextId: "context-2" }),
        );
        assert.equal(observed.chat?.conversationId, "conversation-3");
        await React.act(async () => observed.input!.onExecute("for conversation-3", []));
        assert.equal(executions.length, 1);
        assert.equal(executions[0].conversationId, "conversation-3");
        assert.equal(executions[0].contextId, "context-3");
      }
    }
  } finally {
    await React.act(async () => root.unmount());
    queryClient.clear();
    dom.window.close();
    if (originalWindow) Object.defineProperty(globalThis, "window", originalWindow);
    else Reflect.deleteProperty(globalThis, "window");
    actHost.IS_REACT_ACT_ENVIRONMENT = originalActEnvironment;
  }
}

test("Web and Desktop pass the Agent list schema flag through Chat to Result rendering", () =>
  checkConversationSession("result-schema"));

test("the stored result-only setting reaches Chat and the execution setting command", () =>
  checkConversationSession("result-only"));

test("Web and Desktop hydrate reasoning fragments as one message in completed work", () =>
  checkConversationSession("reasoning-history"));

for (const attachment of ["new", "cached"]) {
  test(`Web and Desktop ${attachment} client preserves unfolded work after onClose`, () =>
    checkConversationSession(`work-close-${attachment}`));
}

test("returning to cached Chat and switching Agent preserves the conversation on send", () =>
  checkConversationSession("restore"));
test("StrictMode cancellation retries Chat hydration before switching Agent and sending", () =>
  checkConversationSession("restore", true));
test("failed history restoration cannot send an existing conversation with a new context", () =>
  checkConversationSession("restore-failure", true));
for (const [kind, name] of [
  [
    "drawer-agent",
    "reopening an Agent drawer before unmount creates a new conversation and context",
  ],
  [
    "drawer-agentflow",
    "reopening an Agentflow drawer before unmount creates a new conversation and context",
  ],
  [
    "route-failure",
    "failed route hydration blocks the old conversation and recovers on navigation",
  ],
  ["route-out-of-order", "late history responses cannot replace the latest route session"],
  ["new-chat", "New Chat replaces both conversation and context identities"],
  ["project-switch", "switching projects starts a fresh conversation and context"],
  ["clear-history", "clearing history preserves conversation and context identities"],
  ["history-refresh", "a running turn refreshes history at its boundaries without polling"],
]) {
  test(name, () => checkConversationSession(kind));
}

test("page reload checks server execution state before enabling the composer", () =>
  checkConversationSession("restore-active"));

test("StrictMode reload preserves the active execution while switching Agent", () =>
  checkConversationSession("restore-active", true));

test("Chat stays interactive during silent retries and blocks only for the visible five", () =>
  checkConversationSession("reconnect"));

test("Chat accepts input during silent retries", () => checkConversationSession("reconnect-send"));
test("switching the browsing directory clears preview and removal restores primary without changing the conversation", () =>
  checkConversationSession("directories"));
