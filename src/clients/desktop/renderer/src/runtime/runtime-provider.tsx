"use client";

import * as React from "react";

import type {
  AgwDesktopBridge,
  DesktopRuntimeState,
  DesktopSettings,
  ServerProfile,
} from "@desktop/shared/contracts";
import { configureApiRuntime, resetApiRuntime } from "@agw/api";
import {
  configureExecutionRuntime,
  executionSessionManager,
  ExecutionPlatformProvider,
} from "@agw/chat";
import { createQueryClient } from "@agw/components";
import { QueryClientProvider, type QueryClient } from "@agw/components/query";
import { getProjectConversations } from "@agw/projects";
import {
  classifyDesktopConnection,
  getActiveServerProfile,
  getEffectiveActiveServerProfile,
  type DesktopConnectionStatus,
  type ServerInfo,
} from "./runtime-model";
import { ServerQueryClientRegistry } from "./query-client-registry";

declare global {
  interface Window {
    agwDesktop?: AgwDesktopBridge;
  }
}

type DesktopRuntimeContextValue = {
  isDesktop: boolean;
  status: DesktopConnectionStatus;
  runtimeState: DesktopRuntimeState | null;
  activeProfile: ServerProfile | null;
  serverInfo: ServerInfo | null;
  error: string | null;
  refresh(): Promise<void>;
  saveSettings(settings: DesktopSettings): Promise<void>;
  saveToken(profileId: string, token: string): Promise<void>;
};

const DesktopRuntimeContext = React.createContext<DesktopRuntimeContextValue | null>(null);

type AuthSession = {
  accessMode?: string;
};

async function probeServer(
  profile: ServerProfile,
  token: string | null,
  signal?: AbortSignal,
): Promise<ServerInfo> {
  const response = await fetch(`${profile.baseUrl.replace(/\/+$/u, "")}/api/server-info`, {
    credentials: "omit",
    headers: token ? { Authorization: `Bearer ${token}` } : undefined,
    signal,
  });
  const body = (await response.json()) as { title?: string; data?: ServerInfo };
  if (!response.ok || !body.data) throw new Error(body.title || "Unable to reach Agw Server.");
  return body.data;
}

async function hasBearerAccess(
  profile: ServerProfile,
  token: string,
  signal?: AbortSignal,
): Promise<boolean> {
  const response = await fetch(`${profile.baseUrl.replace(/\/+$/u, "")}/api/auth/session`, {
    credentials: "omit",
    headers: { Authorization: `Bearer ${token}` },
    signal,
  });
  const body = (await response.json()) as { data?: AuthSession };
  return response.ok && body.data?.accessMode === "bearer";
}

function configureClients(profile: ServerProfile, token: string | null): void {
  const config = { baseUrl: profile.baseUrl, token };
  configureApiRuntime(config);
  configureExecutionRuntime(config);
}

export function DesktopRuntimeProvider({ children }: { children: React.ReactNode }) {
  const isDesktop = typeof window !== "undefined" && Boolean(window.agwDesktop);
  const [status, setStatus] = React.useState<DesktopConnectionStatus>("loading");
  const [runtimeState, setRuntimeState] = React.useState<DesktopRuntimeState | null>(null);
  const [serverInfo, setServerInfo] = React.useState<ServerInfo | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const setupAttempted = React.useRef(new Set<string>());
  const platform = runtimeState?.platform ?? "browser";

  // 每个 profile 最多保留一份 QueryClient；地址或 token 变化会安全冷启动。
  const queryClientRegistryRef = React.useRef<ServerQueryClientRegistry | null>(null);
  if (queryClientRegistryRef.current === null) {
    queryClientRegistryRef.current = new ServerQueryClientRegistry(createQueryClient);
  }
  const queryClientRef = React.useRef<QueryClient | null>(null);
  const [queryClient, setQueryClient] = React.useState<QueryClient>(() => {
    const client = createQueryClient();
    queryClientRef.current = client;
    return client;
  });
  // 每次 connect 递增 generation 并中断上一次连接，防止较慢的旧连接覆盖新连接。
  const connectGenerationRef = React.useRef(0);
  const connectAbortRef = React.useRef<AbortController | null>(null);

  const activateQueryClient = React.useCallback((profile: ServerProfile, token: string | null) => {
    const client = queryClientRegistryRef.current!.get(profile, token);
    if (queryClientRef.current === client) return;
    queryClientRef.current?.cancelQueries();
    queryClientRef.current = client;
    setQueryClient(client);
  }, []);

  React.useEffect(() => {
    const root = document.documentElement;
    root.dataset.agwDesktop = String(isDesktop);
    root.dataset.agwPlatform = platform;

    return () => {
      delete root.dataset.agwDesktop;
      delete root.dataset.agwPlatform;
    };
  }, [isDesktop, platform]);

  // 后台 turn 进入终态时请求主进程显示系统通知；interrupted 多为用户主动停止，不通知。
  // preload 随应用启动注入、不支持热更新，需按方法存在性降级以容忍旧版 Electron 进程。
  // 标题只向事件所属的激活 Server 查询；旧 Server 的残留执行完成时不查，避免打错 Server。
  React.useEffect(() => {
    const bridge = isDesktop ? window.agwDesktop : undefined;
    if (typeof bridge?.showTurnNotification !== "function") return;
    return executionSessionManager.subscribeTurnFinished(({ key, status }) => {
      if (status === "interrupted") return;
      void resolveTurnNotificationTitle(
        key.serverId,
        key.projectId,
        key.contextId,
        activeProfileIdRef.current,
      ).then((title) => {
        void bridge.showTurnNotification({ status, title });
      });
    });
  }, [isDesktop]);

  const connect = React.useCallback(
    async (providedState?: DesktopRuntimeState) => {
      const generation = ++connectGenerationRef.current;
      connectAbortRef.current?.abort();
      const abortController = new AbortController();
      connectAbortRef.current = abortController;

      const bridge = window.agwDesktop;
      if (!bridge) {
        resetApiRuntime();
        configureExecutionRuntime({ baseUrl: "", token: null });
        if (generation === connectGenerationRef.current) setStatus("ready");
        return;
      }

      if (generation === connectGenerationRef.current) {
        setStatus("loading");
        setError(null);
      }
      try {
        let nextState = providedState ?? (await bridge.getRuntimeState());
        if (generation !== connectGenerationRef.current) return;
        queryClientRegistryRef.current!.prune(nextState.settings.profiles.map((item) => item.id));

        let profile = getEffectiveActiveServerProfile(nextState);
        let token = nextState.activeToken;
        configureClients(profile, token);
        activateQueryClient(profile, token);

        let info = await probeServer(profile, token, abortController.signal);
        if (!info.initialized && !setupAttempted.current.has(profile.baseUrl)) {
          setupAttempted.current.add(profile.baseUrl);
          if (generation === connectGenerationRef.current) setStatus("setup-required");
          await bridge.openSetup(profile.baseUrl);
          if (generation !== connectGenerationRef.current) return;

          nextState = await bridge.getRuntimeState();
          if (generation !== connectGenerationRef.current) return;
          queryClientRegistryRef.current!.prune(nextState.settings.profiles.map((item) => item.id));

          profile = getEffectiveActiveServerProfile(nextState);
          token = nextState.activeToken;
          activateQueryClient(profile, token);
          info = await probeServer(profile, token, abortController.signal);
        }

        if (
          info.initialized &&
          token &&
          !(await hasBearerAccess(profile, token, abortController.signal))
        ) {
          token = null;
          nextState = { ...nextState, activeToken: null };
        }

        if (info.initialized && profile.kind === "local" && !token) {
          token = await bridge.provisionLocalToken();
          if (generation !== connectGenerationRef.current) return;

          nextState = { ...nextState, activeToken: token };
        }

        if (generation !== connectGenerationRef.current) return;

        configureClients(profile, token);
        activateQueryClient(profile, token);

        setRuntimeState(nextState);
        setServerInfo(info);
        setStatus(classifyDesktopConnection(profile, info, token));
      } catch (connectionError) {
        if (generation !== connectGenerationRef.current) return;
        if (connectionError instanceof DOMException && connectionError.name === "AbortError")
          return;
        setStatus("unreachable");
        setError(
          connectionError instanceof Error
            ? connectionError.message
            : "Unable to reach Agw Server.",
        );
      }
    },
    [activateQueryClient],
  );

  React.useEffect(() => {
    void connect();
  }, [connect]);

  React.useEffect(
    () => () => {
      connectAbortRef.current?.abort();
      queryClientRegistryRef.current?.dispose();
    },
    [],
  );

  const saveSettings = React.useCallback(
    async (settings: DesktopSettings) => {
      const bridge = window.agwDesktop;
      if (!bridge) return;
      const saved = await bridge.saveSettings(settings);
      // Keep persisted profiles available to the settings UI even when the
      // selected Server cannot be reached by the connection attempt below.
      setRuntimeState(saved);
      queryClientRegistryRef.current!.prune(saved.settings.profiles.map((item) => item.id));
      const previousProfile = runtimeState ? getActiveServerProfile(runtimeState.settings) : null;
      const nextProfile = getActiveServerProfile(saved.settings);
      if (
        previousProfile?.id === nextProfile.id &&
        previousProfile.baseUrl === nextProfile.baseUrl &&
        runtimeState?.activeToken === saved.activeToken
      ) {
        return;
      }
      await connect(saved);
    },
    [connect, runtimeState],
  );

  const saveToken = React.useCallback(
    async (profileId: string, token: string) => {
      const bridge = window.agwDesktop;
      if (!bridge) return;
      await bridge.saveToken(profileId, token);
      await connect();
    },
    [connect],
  );

  const activeProfile = runtimeState ? getEffectiveActiveServerProfile(runtimeState) : null;
  // 通知订阅的 effect 只随 isDesktop 建立一次，经 ref 读取最新的激活 Server。
  const activeProfileIdRef = React.useRef<string | null>(null);
  React.useEffect(() => {
    activeProfileIdRef.current = activeProfile?.id ?? null;
  }, [activeProfile]);
  const value = React.useMemo<DesktopRuntimeContextValue>(
    () => ({
      isDesktop,
      status,
      runtimeState,
      activeProfile,
      serverInfo,
      error,
      refresh: connect,
      saveSettings,
      saveToken,
    }),
    [
      activeProfile,
      connect,
      error,
      isDesktop,
      runtimeState,
      saveSettings,
      saveToken,
      serverInfo,
      status,
    ],
  );

  const handleActiveCountChange = React.useCallback(
    (activeCount: number) => {
      if (isDesktop) void window.agwDesktop?.setActiveTaskCount(activeCount);
    },
    [isDesktop],
  );

  return (
    <DesktopRuntimeContext.Provider value={value}>
      <QueryClientProvider client={queryClient}>
        <ExecutionPlatformProvider
          isDesktop={isDesktop}
          serverId={activeProfile?.id ?? "browser"}
          onActiveCountChange={handleActiveCountChange}
        >
          {children}
        </ExecutionPlatformProvider>
      </QueryClientProvider>
    </DesktopRuntimeContext.Provider>
  );
}

export function useDesktopRuntime(): DesktopRuntimeContextValue {
  const value = React.useContext(DesktopRuntimeContext);
  if (!value) throw new Error("useDesktopRuntime must be used within DesktopRuntimeProvider.");
  return value;
}

const TURN_NOTIFICATION_TITLE_TIMEOUT_MS = 3_000;

async function resolveTurnNotificationTitle(
  serverId: string,
  projectId: string,
  contextId: string,
  activeServerId: string | null,
): Promise<string | undefined> {
  if (!activeServerId || serverId !== activeServerId) return undefined;
  try {
    const summaries = await Promise.race([
      getProjectConversations(projectId),
      new Promise<never>((_, reject) =>
        setTimeout(
          () => reject(new Error("Resolving conversation title timed out.")),
          TURN_NOTIFICATION_TITLE_TIMEOUT_MS,
        ),
      ),
    ]);
    return summaries.find((summary) => summary.contextId === contextId)?.title?.trim() || undefined;
  } catch {
    return undefined;
  }
}
