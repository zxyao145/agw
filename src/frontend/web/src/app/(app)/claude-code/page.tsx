"use client";

import * as React from "react";
import { toast } from "sonner";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import {
  InitMessageContent,
  DirectoryMode,
  PermissionMode,
  LineComment,
  EnvVar,
} from "./types";

import { AiMessage, MessageContentType, ProcessedMessageItem } from "@/types";

import { Ulid, Uuid4 } from "id128";
import { FileExplorer } from "./components/file-explorer";
import { Chat } from "./components/chat/chat";
import { InputArea } from "./components/user-input/input-area";
import type { UserInputRef } from "@/components/message/user-input";
import {
  createSession,
  deleteSessionByThreadId,
  saveSession,
} from "./lib/chat-history-service";
import { Label } from "@/components/ui/label";
import { Badge } from "@/components/ui/badge";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import "./page.css";
import { claudeSettingsStorage } from "./lib/settings-storage";

const gitCodeSource = "./code-work";

export default function ClaudeCodePage() {
  const [workingDirectory, setWorkingDirectory] = React.useState("");
  const [gitAddress, setGitAddress] = React.useState("");
  const [directoryMode, setDirectoryMode] = React.useState<DirectoryMode>(
    DirectoryMode.workingDirectory,
  );
  const [apiKey, setApiKey] = React.useState("");
  const [apiBaseUrl, setApiBaseUrl] = React.useState("");
  const [permissionMode, setPermissionMode] = React.useState<string>(
    PermissionMode.default,
  );
  const [envVars, setEnvVars] = React.useState<EnvVar[]>([]);
  const [isExecuting, setIsExecuting] = React.useState(false);
  const [messages, setMessages] = React.useState<AiMessage[]>([]);
  const [threadId, setThreadId] = React.useState<string | null>(null);
  const [comments, setComments] = React.useState<LineComment[]>([]);
  const [currentTab, setCurrentTab] = React.useState("chat");
  const [showCommentDialog, setShowCommentDialog] = React.useState(false);
  const [isInitStatus, setIsInitStatus] = React.useState(false);
  // const statusRequestPendingRef = React.useRef(false);
  const settingsRequestSessionRef = React.useRef<string | null>(null);

  const handleThreadId = (newThreadId: string | null) => {
    if (newThreadId !== threadId) {
      settingsRequestSessionRef.current = null;
    }
    if (newThreadId) {
      console.debug("Set threadId:", newThreadId);
    }
    setThreadId(newThreadId);
  };

  const [initContent, setInitContent] =
    React.useState<InitMessageContent | null>(null);

  const messagesEndRef = React.useRef<HTMLDivElement>(null!);
  const wsRef = React.useRef<WebSocket | null>(null);
  const userInputRef = React.useRef<UserInputRef | null>(null);
  const topAnchorRef = React.useRef<HTMLDivElement>(null);

  // Auto-scroll to bottom when new messages arrive
  React.useEffect(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [messages]);

  // Load settings from storage on mount
  React.useEffect(() => {
    const {
      workingDirectory: savedWorkingDir,
      gitAddress: savedGitAddress,
      directoryMode: savedDirectoryMode,
      apiKey: savedApiKey,
      apiBaseUrl: savedApiBaseUrl,
      permissionMode: savedPermissionMode,
      envVars: savedEnvVars,
    } = claudeSettingsStorage.get();

    if (savedWorkingDir) setWorkingDirectory(savedWorkingDir);
    if (savedGitAddress) setGitAddress(savedGitAddress);
    if (
      savedDirectoryMode === DirectoryMode.gitAddress ||
      savedDirectoryMode === DirectoryMode.workingDirectory
    ) {
      setDirectoryMode(savedDirectoryMode);
    }
    if (savedApiKey) setApiKey(savedApiKey);
    if (savedApiBaseUrl) setApiBaseUrl(savedApiBaseUrl);
    if (savedPermissionMode) setPermissionMode(savedPermissionMode);
    if (savedEnvVars) setEnvVars(savedEnvVars);
  }, []);

  React.useEffect(() => {
    settingsRequestSessionRef.current = null;
  }, [
    workingDirectory,
    gitAddress,
    directoryMode,
    apiKey,
    apiBaseUrl,
    permissionMode,
    envVars,
  ]);

  const getRepositoryName = React.useCallback((address: string) => {
    const trimmed = address.trim().replace(/\/$/, "");
    if (!trimmed) {
      return "";
    }
    const match = trimmed.match(/([^/:]+?)(?:\.git)?$/);
    return match?.[1] ?? "";
  }, []);

  const getResolvedWorkingDirectory = React.useCallback(
    (sessionId: string | null) => {
      if (directoryMode === DirectoryMode.gitAddress) {
        const repoName = getRepositoryName(gitAddress);
        if (!repoName || !sessionId) {
          return null;
        }
        return `${gitCodeSource}/${repoName}/${sessionId}`;
      }
      return workingDirectory.trim() || null;
    },
    [directoryMode, gitAddress, getRepositoryName, workingDirectory],
  );

  const resolvedWorkingDirectory = React.useMemo(() => {
    if (directoryMode === DirectoryMode.gitAddress) {
      if (!threadId) {
        return "";
      }
      const repoName = getRepositoryName(gitAddress);
      return repoName ? `${gitCodeSource}/${repoName}/${threadId}` : "";
    }
    return workingDirectory;
  }, [
    directoryMode,
    getRepositoryName,
    gitAddress,
    threadId,
    workingDirectory,
  ]);

  // Cleanup WebSocket on unmount
  React.useEffect(() => {
    return () => {
      if (wsRef.current && wsRef.current.readyState === WebSocket.OPEN) {
        wsRef.current.close(1000, "Component unmounted");
      }
    };
  }, []);

  const buildEnvironmentVariables = () => {
    const envObj: Record<string, string> = {};
    if (Array.isArray(envVars)) {
      envVars.forEach((item: { key: string; value: string }) => {
        if (item.key) envObj[item.key] = item.value || "";
      });
    }
    return envObj;
  };

  const buildSettingRequest = (sessionId: string) => {
    return {
      type: 0,
      setting: {
        workingDirectory: getResolvedWorkingDirectory(sessionId),
        gitAddress:
          directoryMode === DirectoryMode.gitAddress
            ? gitAddress.trim() || null
            : null,
        apiKey: apiKey.trim() || null,
        apiBaseUrl: apiBaseUrl.trim() || null,
        systemPrompt: null,
        maxTurns: null,
        sessionId: sessionId,
        permissionMode: permissionMode,
        environmentVariables: buildEnvironmentVariables(),
      },
    };
  };

  const buildInputRequest = (inputMsg: string) => {
    return {
      type: 1,
      input: {
        input: inputMsg,
      },
    };
  };

  const sendSettingIfNeeded = (ws: WebSocket, sessionId: string) => {
    if (settingsRequestSessionRef.current === sessionId) {
      return;
    }
    const settingRequest = buildSettingRequest(sessionId);
    ws.send(JSON.stringify(settingRequest));
    settingsRequestSessionRef.current = sessionId;
  };

  const ensureThreadId = () => {
    if (threadId) {
      return threadId;
    }
    const newThreadId = Uuid4.generate().toCanonical();
    handleThreadId(newThreadId);
    return newThreadId;
  };

  const sendStatusRequest = (ws: WebSocket) => {
    // statusRequestPendingRef.current = true;
    const sessionId = ensureThreadId();
    sendSettingIfNeeded(ws, sessionId);
    setIsInitStatus(true);
    ws.send(JSON.stringify(buildInputRequest("/status")));
  };

  function handleSystem(data: AiMessage) {
    console.log("handleSystem", data);

    var author = data.author;
    if (author === "d-system") {
      var firstContent = data.contents[0];
      if (firstContent.type === MessageContentType.ErrorContent) {
        console.error("something error:", firstContent.content);
        setIsExecuting(false);
        setMessages((prev) => [...prev, data]);
        return;
      }
    }

    if (!data.additionalProperties && !isInitStatus) {
      setMessages((prev) => [...prev, data]);
      return;
    }

    let msgType = "";
    if (data.additionalProperties?.subtype === "init") {
      msgType = "init";
    } else if (data.additionalProperties?.type === "result") {
      msgType = "result";
    } else if (data.additionalProperties?.subtype === "hint") {
      console.log("hint", data);
      return;
    }

    if (msgType === "init") {
      const content = JSON.parse(data.contents[0].content);
      const initContent: InitMessageContent = {
        claudeCodeVersion: content.claude_code_version,
        permissionMode: content.permissionMode,
        model: content.model,

        tools: content.tools,
        slashCommands: content.slash_commands,
        agents: content.agents,
        skills: content.skills,
        plugins: content.plugins,
        mcpServers: content.mcp_servers,
      };

      setInitContent(initContent);
    } else if (msgType === "result") {
      toast.info("Execution completed");
      setIsExecuting(false);
      if (isInitStatus) {
        setIsInitStatus(false);
      } else {
        // setMessages((prev) => [...prev, data]);
      }
    } else {
      setMessages((prev) => [...prev, data]);
    }
  }

  function handleUser(data: AiMessage) {
    setMessages((prev) => [...prev, data]);
  }

  function handleAssistant(data: AiMessage) {
    setMessages((prev) => [...prev, data]);
  }

  const setupWebSocket = () => {
    const protocol = window.location.protocol === "https:" ? "wss:" : "ws:";
    const wsUrl = `${protocol}//${window.location.host}/api/external-agents/claude-code/ws`;

    const ws = new WebSocket(wsUrl);
    wsRef.current = ws;

    ws.onopen = () => {
      console.debug("WebSocket connected");
    };

    ws.onmessage = (event) => {
      try {
        const data: AiMessage = JSON.parse(event.data);
        console.info("onmessage", data);

        if (data.role === "system") {
          handleSystem(data);
        } else if (data.role === "assistant") {
          handleAssistant(data);
        } else if (data.role === "user") {
          handleUser(data);
        }
      } catch (e) {
        console.error("Parse error:", e);
      }
    };

    ws.onerror = (error) => {
      console.error("WebSocket error:", error);
      toast.error("WebSocket connection error");
      setIsExecuting(false);
    };

    ws.onclose = (event) => {
      console.debug("WebSocket closed:", event.code, event.reason);
      wsRef.current = null;
      settingsRequestSessionRef.current = null;
      setIsExecuting(false);

      if (event.code !== 1000) {
        console.error(
          "WebSocket closed unexpectedly:",
          event.code,
          event.reason,
        );
        if (event.code === 1003) {
          toast.error("Invalid request data");
        } else if (event.code === 1011) {
          toast.error("Server error during execution");
        }
      }
    };

    return ws;
  };

  const executeClaudeCode = async (value: string) => {
    if (!value.trim()) {
      toast.error("Please enter a prompt");
      return;
    }

    await sendInputToClaudeCode(value.trim());
  };

  const executeClaudeCodeWithComment = async (value: string) => {
    if (!comments || comments.length === 0) {
      toast.error("Please add comments first");
      return;
    }
    console.debug("comments", comments);

    let prompt = value.trim()
      ? value.trim()
      : "Please make modifications based on the following review comments";
    prompt += "\n\n";

    comments.forEach((comment) => {
      prompt += `file ${comment.filePath}, ${comment.isAfter ? "after" : "before"} the modification, the ${comment.lineIndex}th line: `;
      prompt += comment.content + "\n\n";
    });
    console.debug("Final input with comments:", prompt);
    await sendInputToClaudeCode(prompt);
    setComments([]);
  };

  const waitForWebSocketOpen = (ws: WebSocket): Promise<void> => {
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
  };

  const sendInputToClaudeCode = async (inputMsg: string) => {
    setIsExecuting(true);
    // statusRequestPendingRef.current = false;

    try {
      let ws = wsRef.current;

      if (
        !ws ||
        ws.readyState === WebSocket.CLOSED ||
        ws.readyState === WebSocket.CLOSING
      ) {
        ws = setupWebSocket();
        await waitForWebSocketOpen(ws);
      } else if (ws.readyState === WebSocket.CONNECTING) {
        await waitForWebSocketOpen(ws);
      }

      if (ws.readyState === WebSocket.OPEN) {
        const userMsg: AiMessage = {
          messageId: "",
          author: "user",
          role: "user",
          contents: [
            {
              type: MessageContentType.TextContent,
              content: inputMsg,
            },
          ],
        };
        // Add user message to chat immediately
        setMessages((prev) => [...prev, userMsg]);

        const tid = ensureThreadId();
        sendSettingIfNeeded(ws, tid);
        const request = buildInputRequest(inputMsg);
        console.debug("Sending request:", request);
        ws.send(JSON.stringify(request));
      }
    } catch (error) {
      toast.error(
        `Execute failed: ${error instanceof Error ? error.message : "Unknown error"}`,
      );
      setIsExecuting(false);
    }
  };

  const handleClearSession = async () => {
    const activeThreadId = threadId;
    setMessages([]);
    if (activeThreadId) {
      await saveSession(activeThreadId, []);
    }
  };

  const clearActiveSessionState = () => {
    setMessages([]);
    handleThreadId(null);
    if (wsRef.current) {
      wsRef.current.close(1000, "Session cleared");
      wsRef.current = null;
    }
    settingsRequestSessionRef.current = null;
  };

  const handleSessionSelect = (
    newMessages: AiMessage[],
    newThreadId: string,
  ) => {
    setMessages(newMessages);
    handleThreadId(newThreadId);
    // Close existing WebSocket to start fresh with loaded session
    if (wsRef.current) {
      wsRef.current.close(1000, "Session switched");
      wsRef.current = null;
    }
    settingsRequestSessionRef.current = null;
  };

  const handleSessionDeleted = (deletedThreadId: string) => {
    if (deletedThreadId !== threadId) {
      return;
    }
    clearActiveSessionState();
  };

  const handleAllSessionsCleared = () => {
    if (!threadId) {
      return;
    }
    clearActiveSessionState();
  };

  const handleNewChat = () => {
    void initializeNewChat();
  };

  const initializeNewChat = async () => {
    handleClearSession();

    const newThreadId = Uuid4.generate().toCanonical();
    handleThreadId(newThreadId);

    try {
      await createSession(newThreadId);
    } catch (error) {
      console.error("Failed to create new session:", error);
    }

    try {
      const ws = setupWebSocket();
      await waitForWebSocketOpen(ws);

      if (ws.readyState === WebSocket.OPEN) {
        sendStatusRequest(ws);
      }
    } catch (error) {
      console.error("Failed to initialize status request:", error);
    }
  };

  // Auto-save messages to database when they change
  React.useEffect(() => {
    const msgLength = messages?.length ?? 0;
    console.debug("Messages changed, auto-saving...", threadId, msgLength);
    if (threadId && msgLength > 0) {
      // Debounce saves to avoid too frequent writes
      const timeoutId = setTimeout(async () => {
        // Dynamically import saveSession to avoid SSR issues
        try {
          const { saveSession } = await import("./lib/chat-history-service");
          await saveSession(threadId, messages);
        } catch (error) {
          console.error("Failed to save session:", error);
        }
      }, 1000);

      return () => clearTimeout(timeoutId);
    }
  }, [messages, threadId]);

  const createArr = (key: string, value: string[] | undefined) => {
    return (
      <div className="grid grid-cols-3 items-center py-2 border-b">
        <Label>{key}</Label>
        <div className="col-span-2">
          {!value || value.length === 0
            ? "-"
            : value.map((item, i) => {
                // Handle objects with name/path structure
                const displayValue =
                  typeof item === "object"
                    ? (item as any)?.name || JSON.stringify(item)
                    : item;
                return (
                  <Badge key={i} variant="outline">
                    {displayValue}
                  </Badge>
                );
              })}
        </div>
      </div>
    );
  };

  // Process messages to identify FunctionCall + FunctionResult(s) groups by callId
  const processMessages = (msgs: AiMessage[]): ProcessedMessageItem[] => {
    const items: ProcessedMessageItem[] = [];

    // Track which message indices have been processed
    const processedIndices = new Set<number>();
    const msgLength = msgs?.length ?? 0;
    for (let i = 0; i < msgLength; i++) {
      if (processedIndices.has(i)) {
        continue; // Skip already processed messages
      }

      const currentMsg = msgs[i];

      // Check if current message is a FunctionCall
      const isFunctionCall =
        currentMsg?.contents?.length === 1 &&
        currentMsg.contents[0].type === MessageContentType.FunctionCallContent;

      if (isFunctionCall) {
        const callId = currentMsg.contents[0].additionalProperties
          ?.callId as string;

        if (callId) {
          // Find all FunctionResults with matching callId (anywhere in the message list)
          const matchingResults: { msg: AiMessage; index: number }[] = [];

          for (let j = 0; j < msgs.length; j++) {
            if (j === i || processedIndices.has(j)) continue;

            const msg = msgs[j];
            const isFunctionResult =
              msg?.contents?.length === 1 &&
              msg.contents[0].type === MessageContentType.FunctionResultContent;

            if (isFunctionResult) {
              const resultCallId = msg.contents[0].additionalProperties
                ?.callId as string;
              if (resultCallId === callId) {
                matchingResults.push({ msg, index: j });
              }
            }
          }

          // If we found matching results, create an accordion group
          if (matchingResults.length > 0) {
            const toolName =
              (currentMsg.contents[0].additionalProperties
                ?.toolName as string) ?? "";
            const groupedMessages = [
              currentMsg,
              ...matchingResults.map((r) => r.msg),
            ];

            items.push({
              type: "accordion",
              messages: groupedMessages,
              toolName,
            });

            // Mark all these messages as processed
            processedIndices.add(i);
            matchingResults.forEach((r) => processedIndices.add(r.index));
          } else {
            // FunctionCall without matching results, treat as normal
            items.push({
              type: "normal",
              message: currentMsg,
            });
            processedIndices.add(i);
          }
        } else {
          // FunctionCall without callId, treat as normal
          items.push({
            type: "normal",
            message: currentMsg,
          });
          processedIndices.add(i);
        }
      } else {
        // Check if it's an orphaned FunctionResult
        const isFunctionResult =
          currentMsg?.contents?.length === 1 &&
          currentMsg.contents[0].type ===
            MessageContentType.FunctionResultContent;

        if (isFunctionResult) {
          // This FunctionResult wasn't matched to any FunctionCall
          // (either no callId, or FunctionCall hasn't appeared yet, or already processed)
          items.push({
            type: "normal",
            message: currentMsg,
          });
        } else {
          // Normal message (user, assistant, etc.)
          items.push({
            type: "normal",
            message: currentMsg,
          });
        }
        processedIndices.add(i);
      }
    }

    return items;
  };

  const handleTabChange = (value: string) => {
    // If switching from files to chat and there are unsent comments
    if (value === "chat" && comments && comments.length > 0) {
      setShowCommentDialog(true);
    } else {
      // No unsent comments or switching to files, allow tab change
      setCurrentTab(value);
    }
  };

  const handleConfirmSend = async () => {
    setShowCommentDialog(false);
    const inputValue = userInputRef.current?.value ?? "";
    await executeClaudeCodeWithComment(inputValue);
    userInputRef.current?.setInput("");
    // Tab will switch after comments are sent
    setCurrentTab("chat");
  };

  const handleCancelSend = () => {
    setComments([]);
    setShowCommentDialog(false);
    // Tab will switch after comments are cleared
    setCurrentTab("chat");
  };

  const handleDialogClose = (open: boolean) => {
    if (!open) {
      // Dialog close button clicked, stay on files tab
      setCurrentTab("files");
    }
    setShowCommentDialog(open);
  };

  const handleScrollToTop = () => {
    topAnchorRef.current?.scrollIntoView({
      behavior: "smooth",
      block: "start",
    });
  };

  return (
    <div className="relative flex flex-col h-[calc(100vh-58px)] w-full max-w-8xl mx-auto mr-2">
      <div className="flex-1 overflow-y-auto">
        <div ref={topAnchorRef} />
        <Tabs
          value={currentTab}
          onValueChange={handleTabChange}
          className="w-full h-full"
        >
          <TabsList className="w-fit">
            <TabsTrigger value="chat">Chat</TabsTrigger>
            <TabsTrigger value="files">Files</TabsTrigger>
          </TabsList>

          <TabsContent value="chat" className="flex h-full">
            <Chat
              messages={messages}
              messagesEndRef={messagesEndRef}
              processMessages={processMessages}
              currentThreadId={threadId}
              onSessionSelect={handleSessionSelect}
              onNewChat={handleNewChat}
              onSessionDeleted={handleSessionDeleted}
              onAllSessionsCleared={handleAllSessionsCleared}
            />
          </TabsContent>

          <TabsContent value="files" className="flex h-full">
            <FileExplorer
              rootDirectory={resolvedWorkingDirectory}
              comments={comments}
              setComments={setComments}
            />
          </TabsContent>
        </Tabs>
      </div>

      <div className="absolute bottom-0 z-10 left-0 right-4 h-30 bg-linear-to-t from-bg-000 from-50% via-bg-000/80 via-70% to-transparent pointer-events-none">
        <InputArea
          isExecuting={isExecuting}
          hasMessages={(messages?.length ?? 0) > 0}
          onExecute={executeClaudeCode}
          onExecuteWithComment={executeClaudeCodeWithComment}
          onClearSession={handleClearSession}
          onScrollToTop={handleScrollToTop}
          workingDirectory={workingDirectory}
          setWorkingDirectory={setWorkingDirectory}
          gitAddress={gitAddress}
          setGitAddress={setGitAddress}
          directoryMode={directoryMode}
          setDirectoryMode={setDirectoryMode}
          apiKey={apiKey}
          setApiKey={setApiKey}
          apiBaseUrl={apiBaseUrl}
          setApiBaseUrl={setApiBaseUrl}
          permissionMode={permissionMode}
          setPermissionMode={setPermissionMode}
          envVars={envVars}
          setEnvVars={setEnvVars}
          initContent={initContent}
          createArr={createArr}
          currentTab={currentTab}
          comments={comments}
          userInputRef={userInputRef}
        />
      </div>

      <Dialog open={showCommentDialog} onOpenChange={handleDialogClose}>
        <DialogContent showCloseButton={true}>
          <DialogHeader>
            <DialogTitle>Unsent Comments</DialogTitle>
            <DialogDescription>
              You have {comments.length} unsent comment(s). These will be
              cleared if you don't send them. Would you like to send them now?
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button variant="outline" onClick={handleCancelSend}>
              Clear Comments
            </Button>
            <Button onClick={handleConfirmSend}>Send Comments</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}
