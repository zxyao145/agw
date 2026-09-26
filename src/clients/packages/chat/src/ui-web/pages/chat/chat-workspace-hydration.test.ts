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
import { conversationComposerStorage } from "../../../lib/chat/conversation-composer-storage";
import * as sessionRouting from "./lib/session-routing";
import { getTargetValue } from "./lib/target-options";
import type { ChatProps } from "../../components/message/chat";
import type { ChatWorkspaceProps } from "./chat-workspace";
import { ExecutionReconnectingDialog } from "../../components/message/execution-reconnecting-dialog";
import { QueryClient, QueryClientProvider, useQueryClient } from "@agw/components/query";
import { EMPTY_TOKEN_USAGE } from "@agw/api";
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
  // Chat 最近一次附着时传给会话管理器的回调。The callbacks Chat passed to the session manager on its latest attach.
  let reconnectHandlers: ExecutionHubHandlers | undefined;
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
      onExecute: (text: string, attachments: []) => boolean;
      queue: import("@agw/chat-runtime").ExecutionQueueSnapshot;
      onInputDraftChange?: (value: string) => void;
      userInputRef?: { current: unknown };
      topLeft?: React.ReactNode;
    };
    inputValue?: string;
    newChat?: () => void;
    refreshSignal?: number;
    conversationStatuses?: ReadonlyMap<string, string>;
    currentConversationTurnId?: string | null;
    selectAgent?: (selection: { agentType: number; agentId: string }) => void;
    selectTab?: (value: string) => void;
  } = {};
  const supersededListeners = new Set<(key: { serverId: string; projectId: string }) => void>();
  let activityRequests = 0;
  const executions: ExecutionRequest[] = [];
  const configurations: ExecutionSetting[] = [];
  // 配置与发送按发生顺序记录。Configurations and sends are recorded in the order they happen.
  const commands: string[] = [];
  // 每个对话一条 SignalR 传输：测试从这里写出服务端消息，会话管理器与队列使用真实实现。
  // One SignalR transport per conversation: the test writes server messages here, and the session manager and queue are real.
  const transports: { handlers: ExecutionHubHandlers; setActive: (active: boolean) => void }[] = [];
  const transport = () => transports.at(-1)!;
  const manager = new chatRuntime.ExecutionSessionManager((handlers) => {
    let active = false;
    transports.push({ handlers, setActive: (value) => (active = value) });
    return {
      configure: async (setting: ExecutionSetting) => {
        configurations.push(setting);
        commands.push(
          `configure:${JSON.stringify(setting.environmentVariables ?? {})}:${setting.resultOnly ?? false}`,
        );
        if (kind === "restore-active") {
          await recoveryReady;
          // 页面重新加载后服务端仍在执行原来的 Turn。The server still runs the original turn after the reload.
          active = true;
        }
        return { restoredDurableExecution: false };
      },
      hasActiveExecution: () => active,
      execute: async (request: ExecutionRequest) => {
        executions.push(request);
        commands.push(
          `execute:${request.input.contents.find((content) => content.type === "TextContent")?.content}`,
        );
        active = true;
      },
      listAgentflowCheckpoints: async () => [],
      resumeCheckpoint: async () => "execution-resumed",
      setMode: async () => undefined,
      setPermissionMode: async () => undefined,
      interrupt: async () => undefined,
      interruptAndWait: async () => {
        active = false;
      },
      submitHumanResponse: async () => undefined,
      retryConnection: async () => undefined,
      dispose: async () => undefined,
    };
  });
  const conversationStatuses = manager.conversationStatuses;
  const turnMessage = (
    type: "agw-turn-start" | "agw-turn-finished",
    execution: ExecutionRequest,
  ): import("@agw/api").AiMessage => ({
    messageId: `${type}-${execution.executionId}`,
    role: "system",
    contents: [],
    additionalProperties: {
      type,
      conversationId: execution.conversationId,
      turnId: execution.executionId,
      ...(type === "agw-turn-finished" ? { status: "completed" } : {}),
    },
  });
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
        subscribe: manager.subscribe,
        getSnapshot: manager.getSnapshot,
        subscribeReconnected: manager.subscribeReconnected,
        // 测试直接触发“被取代的 Turn”事件。The test raises the superseded-turn event directly.
        subscribeSupersededTurn: (
          listener: (key: { serverId: string; projectId: string }) => void,
        ) => {
          supersededListeners.add(listener);
          return () => supersededListeners.delete(listener);
        },
        subscribeQueue: manager.subscribeQueue,
        getQueueVersion: manager.getQueueVersion,
        getQueue: (key: chatRuntime.ExecutionSessionKey) => manager.getQueue(key),
        has: (key: chatRuntime.ExecutionSessionKey) => manager.has(key),
        attach: (key: chatRuntime.ExecutionSessionKey, handlers: ExecutionHubHandlers) => {
          reconnectHandlers = handlers;
          return manager.attach(key, handlers);
        },
        retryConnection: (key: chatRuntime.ExecutionSessionKey) => manager.retryConnection(key),
        discard: (key: chatRuntime.ExecutionSessionKey) => manager.discard(key),
        discardProject: (scope: { serverId: string; projectId: string }) =>
          manager.discardProject(scope),
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
          // Record the value Chat writes into the composer when it restores or clears the input.
          if (props.userInputRef) {
            props.userInputRef.current = {
              setInput: (value: string) => {
                observed.inputValue = value;
              },
            };
          }
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
    if (kind === "settings-change") {
      // 当前页面新建的对话：Chat 直接渲染，设置由属性传入。A conversation created on this page: Chat renders directly and settings come in as props.
      const sessionSeed = {
        revision: "settings:1",
        conversationId: null,
        messages: [],
        historyTurns: [],
        usage: EMPTY_TOKEN_USAGE,
        olderMessagesCursor: null,
        hasOlderMessages: false,
        agentMode: null,
      };
      const ChatView = (modules["../../components/message/chat"] as { Chat: typeof Chat }).Chat;
      const renderChat = (environmentVariables: Record<string, string>, resultOnly: boolean) =>
        renderWithQueries(
          React.createElement(ChatView, {
            target: { id: "agent-1", type: "agent" },
            projectId: "project-1",
            conversationId: null,
            active: true,
            sessionSeed,
            environmentVariables,
            resultOnly,
          }),
        );
      const finish = (execution: ExecutionRequest) =>
        React.act(async () => {
          transport().setActive(false);
          transport().handlers.onMessage(turnMessage("agw-turn-finished", execution));
        });
      await React.act(async () => renderChat({ MODE: "old" }, false));
      await React.act(async () => observed.input!.onExecute("first turn", []));
      await React.act(async () =>
        transport().handlers.onMessage(turnMessage("agw-turn-start", executions[0])),
      );
      await finish(executions[0]);

      // 首轮结束后修改设置：下一轮在新设置生效后才发送。Settings change after the first turn: the next turn is sent only once they apply.
      await React.act(async () => renderChat({ MODE: "new" }, true));
      await React.act(async () => observed.input!.onExecute("second turn", []));
      await React.act(async () =>
        transport().handlers.onMessage(turnMessage("agw-turn-start", executions[1])),
      );

      // 运行期间修改设置：提交进入队列，服务端在这一轮结束前收不到设置命令。
      // Settings change during the turn: the submission queues and the server gets no settings command before the turn ends.
      await React.act(async () => renderChat({ MODE: "third" }, true));
      await React.act(async () => observed.input!.onExecute("third turn", []));
      assert.equal(executions.length, 2);
      assert.deepEqual(
        observed.input!.queue.items.map((item) => item.text),
        ["third turn"],
      );
      await finish(executions[1]);

      assert.deepEqual(commands, [
        'configure:{"MODE":"old"}:false',
        "execute:first turn",
        'configure:{"MODE":"new"}:true',
        "execute:second turn",
        'configure:{"MODE":"third"}:true',
        "execute:third turn",
      ]);
      assert.equal(observed.input?.isTransitioning, false);
      assert.deepEqual(errors, []);
      return;
    }
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
          transport().handlers.onMessage(turnMessage("agw-turn-start", executions[0])),
        );
        await React.act(async () =>
          transport().handlers.onMessage({
            messageId: "drawer-result",
            role: "assistant",
            author: "claude-code",
            additionalProperties: { type: "result" },
            contents: [{ type: "TextContent", content: '{"approved":false}' }],
          }),
        );
        await React.act(async () => {
          transport().setActive(false);
          transport().handlers.onMessage(turnMessage("agw-turn-finished", executions[0]));
        });
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
      assert.notEqual(
        executions[1].conversationId,
        executions[0].conversationId,
        "a new drawer session gets a new conversation",
      );
    } else {
      if (kind === "composer") {
        // A draft saved before the page loaded.
        conversationComposerStorage.set(
          { serverId: "local", projectId: "project-1" },
          "conversation-1",
          { input: "saved draft" },
        );
      }
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
        await React.act(async () =>
          transport().handlers.onMessage(turnMessage("agw-turn-start", executions[0])),
        );
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
          for (const message of updates) transport().handlers.onMessage(message);
          // A mode control update flushes the queued output without finishing the turn.
          transport().handlers.onMessage({
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
        await React.act(async () => transport().handlers.onClose?.(new Error("Connection lost")));
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
          transport().setActive(false);
          if (kind === "work-close-cached") {
            transport().handlers.onReconnected?.();
          } else {
            transport().handlers.onMessage(turnMessage("agw-turn-finished", executions[0]));
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
            transport().handlers.onReconnecting?.({
              status: "reconnecting",
              retryAttempt,
              retryDelayMs: 1_000,
            });
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
            await React.act(async () => transport().handlers.onReconnected?.());
            assert.equal(dom.window.document.querySelector('[role="dialog"]'), null);
          }
        }
        await React.act(async () => transport().handlers.onReconnected?.());
        assert.equal(dom.window.document.querySelector('[role="dialog"]'), null);
        assert.equal(dom.window.document.querySelector("[inert]"), null);
        return;
      }
      if (kind === "directories") {
        await React.act(async () => observed.selectTab!("files"));
        const sessionConversationId = observed.chat?.sessionSeed.conversationId;
        assert.deepEqual(observed.chat?.searchDirectoryIds, [null, "extra"]);
        await React.act(async () => observed.explorer!.onFileSelected("README.md"));
        assert.equal(observed.file?.selectedFile, "README.md");
        await React.act(async () => observed.explorer!.onDirectoryChange("extra"));
        assert.equal(observed.file?.selectedFile, null);
        assert.equal(observed.chat?.directoryId, "extra");
        assert.equal(observed.chat?.sessionSeed.conversationId, sessionConversationId);
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
        await React.act(async () => observed.input!.onExecute("waits for the old turn", []));
        assert.equal(executions.length, 0, "input during the old turn waits in the queue");
        assert.deepEqual(
          observed.input?.queue.items.map((item) => item.text),
          ["waits for the old turn"],
        );
        await React.act(async () => {
          transport().setActive(false);
          transport().handlers.onMessage({
            messageId: "old-turn-finished",
            role: "system",
            contents: [],
            additionalProperties: {
              type: "agw-turn-finished",
              status: "completed",
              conversationId: "conversation-1",
              turnId: "old-turn",
            },
          });
        });
        assert.equal(executions.length, 1, "the queued input starts after the old turn completes");
        assert.equal(executions[0].conversationId, "conversation-1");
        assert.deepEqual(observed.input?.queue.items, []);
        return;
      }
      if (kind === "history-refresh") {
        const statusScope = { serverId: "local", projectId: "project-1" };
        assert.equal(activityRequests, 1, "entering the page requests one status snapshot");
        assert.equal(observed.currentConversationTurnId, null);
        const before = observed.refreshSignal!;
        await React.act(async () => observed.input!.onExecute("long answer", []));
        await React.act(async () =>
          transport().handlers.onMessage(turnMessage("agw-turn-start", executions[0])),
        );
        assert.equal(refreshTimers.size, 0, "a running turn does not poll the conversation list");
        assert.equal(observed.refreshSignal, before + 1, "starting a turn refreshes history once");
        assert.equal(observed.conversationStatuses?.get("conversation-1"), "running");
        assert.equal(
          observed.currentConversationTurnId,
          executions[0].executionId,
          "the turn ID lets history fetch the conversation a running turn creates",
        );
        await React.act(async () => {
          transport().setActive(false);
          transport().handlers.onMessage(turnMessage("agw-turn-finished", executions[0]));
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
          assert.equal(executions[0].agentId, "agent-2");
          assert.deepEqual(observed.chat?.sessionSeed.messages, messages);
          assert.equal(configurations.at(-1)?.conversationId, conversation.conversationId);
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
      if (kind === "composer") {
        const scope = { serverId: "local", projectId: "project-1" };
        const agent2 = getTargetValue({ type: "agent", id: "agent-2" });
        const targetId = () => observed.chat?.target?.id;
        async function startNewChat() {
          await React.act(async () => observed.newChat!());
          searchParams = new URLSearchParams(dom.window.location.search);
          await React.act(async () => renderWorkspace());
        }

        assert.equal(targetId(), "agent-1", "without a local choice the server's target applies");
        assert.equal(observed.inputValue, "saved draft", "loading the page restores the draft");
        await React.act(async () => observed.selectAgent!({ agentType: 0, agentId: "agent-2" }));
        await React.act(async () => observed.input!.onInputDraftChange!("edited draft"));

        await navigate("conversation-2");
        await React.act(async () =>
          pending
            .get("conversation-2")!
            .resolve({ ...conversation, conversationId: "conversation-2", contextId: "context-2" }),
        );
        assert.equal(targetId(), "agent-1", "another conversation keeps its own target");
        assert.equal(observed.inputValue, "", "another conversation keeps its own draft");

        await navigate("conversation-1");
        assert.equal(targetId(), "agent-2", "returning restores the conversation's target");
        assert.equal(
          observed.inputValue,
          "edited draft",
          "returning restores the conversation's draft",
        );

        await startNewChat();
        assert.equal(
          targetId(),
          "agent-1",
          "a new conversation without its own choice uses defaults",
        );
        assert.equal(observed.inputValue, "");
        await React.act(async () => observed.selectAgent!({ agentType: 0, agentId: "agent-2" }));
        await React.act(async () => observed.input!.onInputDraftChange!("new conversation draft"));
        await navigate("conversation-1");
        await startNewChat();
        assert.equal(targetId(), "agent-2", "the new conversation keeps its own target");
        assert.equal(observed.inputValue, "new conversation draft");

        // UserInput reports the empty value right after handing the text to onExecute.
        await React.act(async () => {
          observed.input!.onExecute("new conversation draft", []);
          observed.input!.onInputDraftChange!("");
        });
        assert.equal(executions.length, 1);
        assert.equal(executions[0].agentId, "agent-2");
        assert.deepEqual(
          conversationComposerStorage.get(scope, executions[0].conversationId),
          {},
          "a draft conversation ID is not a saved conversation yet",
        );
        assert.equal(observed.chat?.conversationId, null);
        await React.act(async () => observed.input!.onInputDraftChange!("typed after sending"));
        await React.act(async () =>
          transport().handlers.onMessage(turnMessage("agw-turn-start", executions[0])),
        );
        assert.deepEqual(
          conversationComposerStorage.get(scope, executions[0].conversationId),
          { targetValue: agent2, input: "typed after sending" },
          "acceptance hands the target and the newer draft to the new conversation ID",
        );
        assert.deepEqual(conversationComposerStorage.get(scope, null), {});
        assert.equal(observed.chat?.conversationId, executions[0].conversationId);
        assert.equal(
          new URLSearchParams(dom.window.location.search).get("conversationId"),
          executions[0].conversationId,
        );
        return;
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
        } else {
          assert.notEqual(executions[0].conversationId, "conversation-1");
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

test("changed chat settings apply before the next turn, and during a turn wait for it to end", () =>
  checkConversationSession("settings-change"));

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
test("failed history restoration cannot send an existing conversation before its history loads", () =>
  checkConversationSession("restore-failure", true));
for (const [kind, name] of [
  ["drawer-agent", "reopening an Agent drawer before unmount creates a new conversation"],
  ["drawer-agentflow", "reopening an Agentflow drawer before unmount creates a new conversation"],
  [
    "route-failure",
    "failed route hydration blocks the old conversation and recovers on navigation",
  ],
  ["route-out-of-order", "late history responses cannot replace the latest route session"],
  ["new-chat", "New Chat replaces the conversation identity"],
  ["project-switch", "switching projects starts a fresh conversation"],
  ["clear-history", "clearing history preserves the conversation identity"],
  ["history-refresh", "a running turn refreshes history at its boundaries without polling"],
  ["composer", "each conversation keeps its own local Agent choice and input draft"],
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
