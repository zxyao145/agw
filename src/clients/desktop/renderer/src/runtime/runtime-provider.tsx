"use client";

import * as React from "react";

import type {
  AgwDesktopBridge,
  DesktopRuntimeState,
  DesktopSettings,
  ServerProfile,
} from "@desktop/shared/contracts";
import { configureApiRuntime, resetApiRuntime } from "@agw/api";
import { configureExecutionRuntime, ExecutionPlatformProvider } from "@agw/chat";
import {
  classifyDesktopConnection,
  getActiveServerProfile,
  getEffectiveActiveServerProfile,
  type DesktopConnectionStatus,
  type ServerInfo,
} from "./runtime-model";

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

async function probeServer(profile: ServerProfile, token: string | null): Promise<ServerInfo> {
  const response = await fetch(`${profile.baseUrl.replace(/\/+$/u, "")}/api/server-info`, {
    credentials: "omit",
    headers: token ? { Authorization: `Bearer ${token}` } : undefined,
  });
  const body = (await response.json()) as { title?: string; data?: ServerInfo };
  if (!response.ok || !body.data) throw new Error(body.title || "Unable to reach Agw Server.");
  return body.data;
}

async function hasBearerAccess(profile: ServerProfile, token: string): Promise<boolean> {
  const response = await fetch(`${profile.baseUrl.replace(/\/+$/u, "")}/api/auth/session`, {
    credentials: "omit",
    headers: { Authorization: `Bearer ${token}` },
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

  React.useEffect(() => {
    const root = document.documentElement;
    root.dataset.agwDesktop = String(isDesktop);
    root.dataset.agwPlatform = platform;

    return () => {
      delete root.dataset.agwDesktop;
      delete root.dataset.agwPlatform;
    };
  }, [isDesktop, platform]);

  const connect = React.useCallback(async (providedState?: DesktopRuntimeState) => {
    const bridge = window.agwDesktop;
    if (!bridge) {
      resetApiRuntime();
      configureExecutionRuntime({ baseUrl: "", token: null });
      setStatus("ready");
      return;
    }

    setStatus("loading");
    setError(null);
    try {
      let nextState = providedState ?? (await bridge.getRuntimeState());
      setRuntimeState(nextState);
      let profile = getEffectiveActiveServerProfile(nextState);
      let token = nextState.activeToken;
      configureClients(profile, token);

      let info = await probeServer(profile, token);
      if (!info.initialized && !setupAttempted.current.has(profile.baseUrl)) {
        setupAttempted.current.add(profile.baseUrl);
        setStatus("setup-required");
        await bridge.openSetup(profile.baseUrl);
        nextState = await bridge.getRuntimeState();
        profile = getEffectiveActiveServerProfile(nextState);
        token = nextState.activeToken;
        info = await probeServer(profile, token);
      }

      if (info.initialized && token && !(await hasBearerAccess(profile, token))) {
        token = null;
        nextState = { ...nextState, activeToken: null };
      }

      if (info.initialized && profile.kind === "local" && !token) {
        token = await bridge.provisionLocalToken();
        nextState = { ...nextState, activeToken: token };
      }

      configureClients(profile, token);

      setRuntimeState(nextState);
      setServerInfo(info);
      setStatus(classifyDesktopConnection(profile, info, token));
    } catch (connectionError) {
      setStatus("unreachable");
      setError(
        connectionError instanceof Error ? connectionError.message : "Unable to reach Agw Server.",
      );
    }
  }, []);

  React.useEffect(() => {
    void connect();
  }, [connect]);

  const saveSettings = React.useCallback(
    async (settings: DesktopSettings) => {
      const bridge = window.agwDesktop;
      if (!bridge) return;
      const saved = await bridge.saveSettings(settings);
      const previousProfile = runtimeState ? getActiveServerProfile(runtimeState.settings) : null;
      const nextProfile = getActiveServerProfile(saved.settings);
      if (
        previousProfile?.id === nextProfile.id &&
        previousProfile.baseUrl === nextProfile.baseUrl &&
        runtimeState?.activeToken === saved.activeToken
      ) {
        setRuntimeState(saved);
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
      <ExecutionPlatformProvider
        isDesktop={isDesktop}
        serverId={activeProfile?.id ?? "browser"}
        onActiveCountChange={handleActiveCountChange}
      >
        {children}
      </ExecutionPlatformProvider>
    </DesktopRuntimeContext.Provider>
  );
}

export function useDesktopRuntime(): DesktopRuntimeContextValue {
  const value = React.useContext(DesktopRuntimeContext);
  if (!value) throw new Error("useDesktopRuntime must be used within DesktopRuntimeProvider.");
  return value;
}
