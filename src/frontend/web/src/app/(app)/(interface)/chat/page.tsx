"use client";

import * as React from "react";
import { Uuid4 } from "id128";
import { useQuery } from "@tanstack/react-query";
import { useRouter, useSearchParams } from "next/navigation";
import { toast } from "sonner";

import { apiGet, ApiError } from "@/api/client";
import { getTaskDetails } from "@/api/task-client";
import { Conversation } from "@/components/message/conversation";
import { type UserInputRef } from "@/components/message/user-input";
import { TaskHistoryList } from "@/components/task/task-list";
import { Card } from "@/components/ui/card";
import {
  Select,
  SelectContent,
  SelectGroup,
  SelectItem,
  SelectLabel,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { createUserTextMessage, mergeStreamingMessagesById, toExecutionWsUserInput } from "@/lib/execution-stream";
import type { AiMessage } from "@/types";
import { InputArea } from "./components/user-input/input-area";
import { handleAiMessage, type AiMessageAction } from "./lib/ai-message-handlers";
import type { ChatTargetOption } from "./types";

type ProjectDto = {
  id: string;
  name: string;
  enable: boolean;
};

type AgentDto = {
  id: string;
  displayName: string;
  name: string;
};

type AgentflowDto = {
  id: string;
  name: string;
  enable?: boolean;
};

function getTargetValueFromMetadata(targetType: unknown, targetId: unknown): string | null {
  if ((targetType !== "agent" && targetType !== "agentflow") || typeof targetId !== "string") {
    return null;
  }

  const trimmedTargetId = targetId.trim();
  if (!trimmedTargetId) {
    return null;
  }

  return `${targetType}:${trimmedTargetId}`;
}

function getRestoredTargetValue(messages: AiMessage[]): string | null {
  for (let messageIndex = messages.length - 1; messageIndex >= 0; messageIndex -= 1) {
    const message = messages[messageIndex];
    if (message.role !== "user") {
      continue;
    }

    for (let contentIndex = message.contents.length - 1; contentIndex >= 0; contentIndex -= 1) {
      const content = message.contents[contentIndex];
      const restoredValue = getTargetValueFromMetadata(
        content.additionalProperties?.targetType,
        content.additionalProperties?.targetId,
      );

      if (restoredValue) {
        return restoredValue;
      }
    }
  }

  return null;
}

function getApiErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (typeof error.body === "string" && error.body.trim().length > 0) {
      return error.body;
    }

    return `${error.status} ${error.statusText}`;
  }

  if (error instanceof Error) {
    return error.message;
  }

  return "Unknown error";
}

function getTargetValue(target: ChatTargetOption): string {
  return `${target.type}:${target.id}`;
}

function nextTaskId(): string {
  return Uuid4.generate().toCanonical();
}

export default function ChatPage() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const queryProjectId = searchParams.get("projectId");
  const queryTaskId = searchParams.get("taskId");

  const [selectedProjectId, setSelectedProjectId] = React.useState<string | null>(queryProjectId);
  const [selectedTargetValue, setSelectedTargetValue] = React.useState<string | null>(null);
  const [messages, setMessages] = React.useState<AiMessage[]>([]);
  const [taskId, setTaskId] = React.useState<string | null>(queryTaskId);
  const [isExecuting, setIsExecuting] = React.useState(false);

  const wsRef = React.useRef<WebSocket | null>(null);
  const messagesStartRef = React.useRef<HTMLDivElement>(null!);
  const messagesEndRef = React.useRef<HTMLDivElement>(null!);
  const userInputRef = React.useRef<UserInputRef | null>(null);
  const hydratedTaskKeyRef = React.useRef<string | null>(null);

  const projectsQuery = useQuery({
    queryKey: ["projects"],
    queryFn: async () => (await apiGet("/api/projects")) as ProjectDto[],
  });

  const agentsQuery = useQuery({
    queryKey: ["agents"],
    queryFn: async () =>
      (await apiGet("/api/agents")) as Array<AgentDto>,
  });

  const agentflowsQuery = useQuery({
    queryKey: ["agentflows"],
    queryFn: async () => (await apiGet("/api/agentflows")) as Array<AgentflowDto>,
  });

  const projects = React.useMemo(
    () => (projectsQuery.data ?? []).filter((project) => project.enable),
    [projectsQuery.data],
  );

  const targetOptions = React.useMemo<ChatTargetOption[]>(() => {
    const agentOptions =
      agentsQuery.data?.map((agent) => ({
        id: agent.id,
        label: agent.displayName?.trim() || agent.name,
        type: "agent" as const,
      })) ?? [];

    const agentflowOptions =
      agentflowsQuery.data
        ?.filter((agentflow) => agentflow.enable ?? true)
        .map((agentflow) => ({
          id: agentflow.id,
          label: agentflow.name,
          type: "agentflow" as const,
        })) ?? [];

    return [...agentOptions, ...agentflowOptions].sort((left, right) =>
      left.label.localeCompare(right.label),
    );
  }, [agentflowsQuery.data, agentsQuery.data]);

  const selectedTarget = React.useMemo(
    () =>
      targetOptions.find((option) => getTargetValue(option) === selectedTargetValue) ?? null,
    [selectedTargetValue, targetOptions],
  );

  const closeSocket = React.useCallback((reason: string) => {
    const ws = wsRef.current;
    if (!ws) {
      return;
    }

    wsRef.current = null;
    if (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING) {
      ws.close(1000, reason);
    }
  }, []);

  const syncRoute = React.useCallback(
    (projectId: string | null, taskIdValue: string | null) => {
      const nextParams = new URLSearchParams();
      if (projectId) {
        nextParams.set("projectId", projectId);
      }
      if (projectId && taskIdValue) {
        nextParams.set("taskId", taskIdValue);
      }

      const nextQuery = nextParams.toString();
      router.replace(nextQuery ? `/chat?${nextQuery}` : "/chat", { scroll: false });
    },
    [router],
  );

  const applyAiMessageActions = React.useCallback((actions: AiMessageAction[]) => {
    const pendingMessages: AiMessage[] = [];

    actions.forEach((action) => {
      switch (action.type) {
        case "append":
          pendingMessages.push(action.message);
          break;
        case "setIsExecuting":
          setIsExecuting(action.value);
          break;
        default:
          break;
      }
    });

    if (pendingMessages.length > 0) {
      setMessages((prev) => mergeStreamingMessagesById([...prev, ...pendingMessages]));
    }
  }, []);

  const isTurnFinishedMessage = React.useCallback((message: AiMessage): boolean => {
    if (message.role?.toLowerCase() !== "system") {
      return false;
    }

    if (message.author !== "$agw-server") {
      return false;
    }

    return message.contents.some(
      (content) => content.additionalProperties?.type === "turn-finished",
    );
  }, []);

  const waitForWebSocketOpen = React.useCallback((ws: WebSocket): Promise<void> => {
    if (ws.readyState === WebSocket.OPEN) {
      return Promise.resolve();
    }

    return new Promise<void>((resolve, reject) => {
      const onOpen = () => {
        ws.removeEventListener("open", onOpen);
        ws.removeEventListener("error", onError);
        resolve();
      };

      const onError = () => {
        ws.removeEventListener("open", onOpen);
        ws.removeEventListener("error", onError);
        reject(new Error("Failed to connect"));
      };

      ws.addEventListener("open", onOpen);
      ws.addEventListener("error", onError);
    });
  }, []);

  const setupWebSocket = React.useCallback(
    (executionId: string) => {
      const protocol = window.location.protocol === "https:" ? "wss:" : "ws:";
      const ws = new WebSocket(`${protocol}//${window.location.host}/api/executions/${executionId}/ws`);
      wsRef.current = ws;

      ws.onmessage = (event) => {
        try {
          const message = JSON.parse(event.data as string) as AiMessage;
          if (isTurnFinishedMessage(message)) {
            setIsExecuting(false);
            return;
          }

          applyAiMessageActions(handleAiMessage(message));
        } catch (error) {
          console.error("Parse error:", error);
        }
      };

      ws.onerror = () => {
        toast.error("WebSocket connection error");
        setIsExecuting(false);
      };

      ws.onclose = (event) => {
        wsRef.current = null;
        setIsExecuting(false);

        if (event.code !== 1000) {
          if (event.code === 1003) {
            toast.error("Invalid request data");
          } else if (event.code === 1007) {
            toast.error(event.reason || "Invalid request payload");
          } else if (event.code === 1011) {
            toast.error("Server error during execution");
          }
        }
      };

      return ws;
    },
    [applyAiMessageActions, isTurnFinishedMessage],
  );

  const buildSettingRequest = React.useCallback(
    (nextTaskIdValue: string) => ({
      type: "SettingCommand",
      settingContent: "{}",
      projectId: selectedProjectId,
      taskId: nextTaskIdValue,
    }),
    [selectedProjectId],
  );

  const buildExecRequest = React.useCallback(
    (message: AiMessage) => ({
      type: "ExecCommand",
      agentType: selectedTarget?.type === "agent" ? 0 : 1,
      input: toExecutionWsUserInput(message),
    }),
    [selectedTarget],
  );

  const ensureTaskId = React.useCallback(() => {
    if (taskId) {
      return taskId;
    }

    const nextId = nextTaskId();
    setTaskId(nextId);
    syncRoute(selectedProjectId, nextId);
    return nextId;
  }, [selectedProjectId, syncRoute, taskId]);

  const clearActiveSessionState = React.useCallback(() => {
    closeSocket("Session cleared");
    hydratedTaskKeyRef.current = null;
    setIsExecuting(false);
    setMessages([]);
    setTaskId(null);
    userInputRef.current?.setInput("");
  }, [closeSocket]);

  const resetSession = React.useCallback(() => {
    clearActiveSessionState();
    syncRoute(selectedProjectId, null);
  }, [clearActiveSessionState, selectedProjectId, syncRoute]);

  const loadTaskHistory = React.useCallback(
    async (projectId: string, nextTaskIdValue: string) => {
      const details = await getTaskDetails(projectId, nextTaskIdValue);
      const restoredTargetValue = getRestoredTargetValue(details.messages ?? []);

      closeSocket("Session switched");
      hydratedTaskKeyRef.current = `${projectId}:${details.taskId}`;
      setSelectedProjectId(projectId);
      setIsExecuting(false);
      setTaskId(details.taskId);
      setMessages(details.messages ?? []);
      if (restoredTargetValue) {
        setSelectedTargetValue(restoredTargetValue);
      }
      syncRoute(projectId, details.taskId);
    },
    [closeSocket, syncRoute],
  );

  React.useEffect(() => {
    return () => {
      closeSocket("Component unmounted");
    };
  }, [closeSocket]);

  React.useEffect(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [messages]);

  React.useEffect(() => {
    if (projects.length === 0) {
      setSelectedProjectId(null);
      return;
    }

    setSelectedProjectId((current) => {
      if (current && projects.some((project) => project.id === current)) {
        return current;
      }

      if (queryProjectId && projects.some((project) => project.id === queryProjectId)) {
        return queryProjectId;
      }

      return projects[0].id;
    });
  }, [projects, queryProjectId]);

  React.useEffect(() => {
    if (targetOptions.length === 0) {
      setSelectedTargetValue(null);
      return;
    }

    setSelectedTargetValue((current) => {
      if (current) {
        return current;
      }

      return getTargetValue(targetOptions[0]);
    });
  }, [targetOptions]);

  React.useEffect(() => {
    if (!queryProjectId) {
      clearActiveSessionState();
      return;
    }

    if (!queryTaskId) {
      clearActiveSessionState();
      setSelectedProjectId(queryProjectId);
      return;
    }

    const hydrateKey = `${queryProjectId}:${queryTaskId}`;
    if (hydratedTaskKeyRef.current === hydrateKey) {
      return;
    }

    let cancelled = false;

    void (async () => {
      try {
        const details = await getTaskDetails(queryProjectId, queryTaskId);
        const restoredTargetValue = getRestoredTargetValue(details.messages ?? []);
        if (cancelled) {
          return;
        }

        closeSocket("History loaded");
        hydratedTaskKeyRef.current = hydrateKey;
        setSelectedProjectId(queryProjectId);
        setIsExecuting(false);
        setTaskId(details.taskId);
        setMessages(details.messages ?? []);
        if (restoredTargetValue) {
          setSelectedTargetValue(restoredTargetValue);
        }
      } catch (error) {
        if (!cancelled) {
          toast.error(`Failed to load task history: ${getApiErrorMessage(error)}`);
        }
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [clearActiveSessionState, closeSocket, queryProjectId, queryTaskId]);

  const handleProjectChange = React.useCallback(
    (nextProjectId: string) => {
      if (nextProjectId === selectedProjectId) {
        return;
      }

      closeSocket("Project switched");
      hydratedTaskKeyRef.current = null;
      setIsExecuting(false);
      setSelectedProjectId(nextProjectId);
      setMessages([]);
      setTaskId(null);
      syncRoute(nextProjectId, null);
    },
    [closeSocket, selectedProjectId, syncRoute],
  );

  const handleTargetChange = React.useCallback(
    (nextTargetValue: string) => {
      if (nextTargetValue === selectedTargetValue) {
        return;
      }

      closeSocket("Target switched");
      setIsExecuting(false);
      setSelectedTargetValue(nextTargetValue);
    },
    [closeSocket, selectedTargetValue],
  );

  const handleExecute = React.useCallback(
    async (value: string) => {
      const trimmedValue = value.trim();
      if (!trimmedValue) {
        toast.error("Please enter a prompt");
        return;
      }

      if (!selectedProjectId) {
        toast.error("Please select a project");
        return;
      }

      if (!selectedTarget) {
        toast.error("Please select a target");
        return;
      }

      setIsExecuting(true);

      try {
        let ws = wsRef.current;
        if (!ws || ws.readyState === WebSocket.CLOSED || ws.readyState === WebSocket.CLOSING) {
          ws = setupWebSocket(selectedTarget.id);
          await waitForWebSocketOpen(ws);
        } else if (ws.readyState === WebSocket.CONNECTING) {
          await waitForWebSocketOpen(ws);
        }

        if (ws.readyState !== WebSocket.OPEN) {
          throw new Error("WebSocket is not open");
        }

        const userMessage = createUserTextMessage(trimmedValue);
        const firstContent = userMessage.contents[0];
        if (firstContent) {
          firstContent.additionalProperties = {
            ...firstContent.additionalProperties,
            targetType: selectedTarget.type,
            targetId: selectedTarget.id,
          };
        }

        setMessages((prev) => [...prev, userMessage]);

        const nextTaskIdValue = ensureTaskId();
        ws.send(JSON.stringify(buildSettingRequest(nextTaskIdValue)));
        ws.send(JSON.stringify(buildExecRequest(userMessage)));
      } catch (error) {
        toast.error(`Execute failed: ${getApiErrorMessage(error)}`);
        setIsExecuting(false);
      }
    },
    [
      buildExecRequest,
      buildSettingRequest,
      ensureTaskId,
      selectedProjectId,
      selectedTarget,
      setupWebSocket,
      waitForWebSocketOpen,
    ],
  );

  const handleInterrupt = React.useCallback(() => {
    const ws = wsRef.current;
    if (!ws || ws.readyState !== WebSocket.OPEN) {
      toast.error("No active session to interrupt");
      setIsExecuting(false);
      return;
    }

    ws.send(
      JSON.stringify({
        type: "InterruptCommand",
        reason: "Stop requested by user.",
      }),
    );
    closeSocket("Stop requested by user.");
    setIsExecuting(false);
  }, [closeSocket]);

  const handleTaskSelect = React.useCallback(
    async (nextTaskIdValue: string) => {
      if (!selectedProjectId) {
        toast.error("Please select a project");
        return;
      }

      try {
        await loadTaskHistory(selectedProjectId, nextTaskIdValue);
      } catch (error) {
        toast.error(`Failed to load session: ${getApiErrorMessage(error)}`);
      }
    },
    [loadTaskHistory, selectedProjectId],
  );

  const handleTaskDeleted = React.useCallback(
    (deletedTaskId: string) => {
      if (deletedTaskId === taskId) {
        resetSession();
      }
    },
    [resetSession, taskId],
  );

  const handleScrollToTop = React.useCallback(() => {
    messagesStartRef.current?.scrollIntoView({
      behavior: "smooth",
      block: "start",
    });
  }, []);

  return (
    <div className="flex h-[calc(100vh-58px)] w-full min-w-0 flex-col gap-4 px-2 md:px-0 md:pr-2">
      <div className="flex flex-col gap-3 md:flex-row md:items-center md:justify-between">
        <div className="space-y-1">
          <h1 className="text-xl font-semibold">Chat</h1>
          <p className="text-sm text-muted-foreground">
            Select a project and target, then continue an existing task or start a new session.
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <Select value={selectedProjectId ?? undefined} onValueChange={handleProjectChange}>
            <SelectTrigger className="w-[220px]" aria-label="Select project">
              <SelectValue placeholder="Select project" />
            </SelectTrigger>
            <SelectContent position="popper" side="bottom" align="start" sideOffset={4}>
              {projects.map((project) => (
                <SelectItem key={project.id} value={project.id}>
                  {project.name}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>

          <Select value={selectedTargetValue ?? undefined} onValueChange={handleTargetChange}>
            <SelectTrigger className="w-[260px]" aria-label="Select target">
              <SelectValue placeholder="Select agent or agentflow" />
            </SelectTrigger>
            <SelectContent position="popper" side="bottom" align="start" sideOffset={4}>
              <SelectGroup>
                <SelectLabel>Agent</SelectLabel>
                {targetOptions
                  .filter((option) => option.type === "agent")
                  .map((option) => (
                    <SelectItem key={getTargetValue(option)} value={getTargetValue(option)}>
                      {option.label}
                    </SelectItem>
                  ))}
              </SelectGroup>
              <SelectGroup>
                <SelectLabel>Agentflow</SelectLabel>
                {targetOptions
                  .filter((option) => option.type === "agentflow")
                  .map((option) => (
                    <SelectItem key={getTargetValue(option)} value={getTargetValue(option)}>
                      {option.label}
                    </SelectItem>
                  ))}
              </SelectGroup>
            </SelectContent>
          </Select>
        </div>
      </div>

      {projectsQuery.isError || agentsQuery.isError || agentflowsQuery.isError ? (
        <div className="text-sm text-destructive">
          Failed to load chat dependencies:{" "}
          {getApiErrorMessage(projectsQuery.error ?? agentsQuery.error ?? agentflowsQuery.error)}
        </div>
      ) : null}

      <div className="grid min-h-0 flex-1 gap-4 lg:grid-cols-[320px_minmax(0,1fr)]">
        <Card className="min-h-[320px] overflow-hidden">
          <TaskHistoryList
            projectId={selectedProjectId ?? ""}
            currentTaskId={taskId}
            onTaskSelect={(nextTaskIdValue) => {
              void handleTaskSelect(nextTaskIdValue);
            }}
            onNewTask={resetSession}
            onTaskDeleted={handleTaskDeleted}
            onAllTasksDeleted={resetSession}
          />
        </Card>

        <Card className="relative min-h-[420px] overflow-hidden">
          <div className="border-b px-4 py-3">
            <div className="text-sm font-medium">{selectedTarget?.label ?? "No target selected"}</div>
            <div className="text-xs text-muted-foreground">
              {selectedProjectId
                ? `Project: ${projects.find((project) => project.id === selectedProjectId)?.name ?? selectedProjectId}`
                : "Select a project to begin"}
              {selectedTarget ? ` · Target: ${selectedTarget.type}` : ""}
              {taskId ? ` · Task: ${taskId}` : ""}
            </div>
          </div>

          <div className="relative flex h-[calc(100%-57px)] min-h-0 flex-col">
            <Conversation
              taskId={taskId}
              messages={messages}
              messagesStartRef={messagesStartRef}
              messagesEndRef={messagesEndRef}
            />

            <div className="pointer-events-none absolute bottom-0 left-0 right-0 z-10 h-30 bg-linear-to-t from-bg-000 from-50% via-bg-000/80 via-70% to-transparent px-2">
              <InputArea
                isExecuting={isExecuting}
                hasMessages={messages.length > 0}
                onExecute={(value) => {
                  void handleExecute(value);
                }}
                onInterrupt={handleInterrupt}
                onClearSession={resetSession}
                onScrollToTop={handleScrollToTop}
                userInputRef={userInputRef}
              />
            </div>
          </div>
        </Card>
      </div>
    </div>
  );
}
