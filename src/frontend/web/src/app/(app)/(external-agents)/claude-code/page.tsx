"use client";

import * as React from "react";
import dynamic from "next/dynamic";
import { toast } from "sonner";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { InitMessageContent, DirectoryMode, PermissionMode, LineComment, EnvVar } from "./types";

import { AiMessage, MessageContentType, ProcessedMessageItem } from "@/types";
import { createUserTextMessage, toExecutionWsUserInput } from "@/lib/execution-stream";

import { Uuid4 } from "id128";
import { InputArea } from "./components/user-input/input-area";
import type { UserInputRef } from "@/components/message/user-input";
import { ChatSession } from "@/components/message/chat-session";
import {
  CLAUDE_CODE_PROJECT_ID,
  getSessionBySessionId,
  type ChatSessionRecordDetails,
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
import { Drawer, DrawerContent, DrawerHeader, DrawerTitle } from "@/components/ui/drawer";
import { PanelLeftClose, PanelLeftOpen } from "lucide-react";
import { getFileDiff, readFile, type GitDiffResponse } from "@/api/files";
import ColResizeSplit from "./components/split-layout";
import Export from "./components/file-explorer/explorer";
import NoSelectedFile from "./components/file-explorer/no-selected-file";
import FileHeader from "./components/file-explorer/file-header";
import FileLoading from "./components/file-explorer/file-loading";
import FileError from "./components/file-explorer/file-error";
import FileViewer from "./components/file-explorer/file-viewer";
import UnChangedFile from "./components/file-explorer/unchanged-file";
import { DiffViewer } from "./components/file-explorer/diff-viewer";
import "./page.css";
import { claudeSettingsStorage } from "./lib/settings-storage";
import { type AiMessageAction, handleAiMessage } from "./lib/ai-message-handlers";

const gitCodeSource = "./code-work";

const ChatHistoryList = dynamic(
  () =>
    import("./components/chat/chat-history-list").then((mod) => ({
      default: mod.ChatHistoryList,
    })),
  { ssr: false },
);

export default function ClaudeCodePage() {
  const [workingDirectory, setWorkingDirectory] = React.useState("");
  const [gitAddress, setGitAddress] = React.useState("");
  const [directoryMode, setDirectoryMode] = React.useState<DirectoryMode>(
    DirectoryMode.workingDirectory,
  );
  const [apiKey, setApiKey] = React.useState("");
  const [apiBaseUrl, setApiBaseUrl] = React.useState("");
  const [permissionMode, setPermissionMode] = React.useState<string>(PermissionMode.default);
  const [envVars, setEnvVars] = React.useState<EnvVar[]>([]);
  const [isExecuting, setIsExecuting] = React.useState(false);
  const [messages, setMessages] = React.useState<AiMessage[]>([]);
  const [sessionId, setSessionId] = React.useState<string | null>(null);
  const [comments, setComments] = React.useState<LineComment[]>([]);
  const [currentTab, setCurrentTab] = React.useState("chat");
  const [showCommentDialog, setShowCommentDialog] = React.useState(false);
  const [isInitStatus, setIsInitStatus] = React.useState(false);
  const [isMobile, setIsMobile] = React.useState(false);
  const [isDrawerOpen, setIsDrawerOpen] = React.useState(false);
  const [drawerContent, setDrawerContent] = React.useState<"chat" | "files" | null>(null);
  const [showChatHistory, setShowChatHistory] = React.useState(true);
  const [showFileExplorer, setShowFileExplorer] = React.useState(true);
  const [selectedFile, setSelectedFile] = React.useState<string | null>(null);
  const [fileContent, setFileContent] = React.useState<string>("");
  const [isLoadingContent, setIsLoadingContent] = React.useState(false);
  const [contentError, setContentError] = React.useState<string | null>(null);
  const [onlyDiff, setOnlyDiff] = React.useState(true);
  const [recursiveMode, setRecursiveMode] = React.useState(true);
  const [diffContentData, setDiffContentData] = React.useState<GitDiffResponse | null>(null);
  // const statusRequestPendingRef = React.useRef(false);
  const settingsRequestSessionRef = React.useRef<string | null>(null);

  const applyAiMessageActions = React.useCallback((actions: AiMessageAction[]) => {
    const pendingMessages: AiMessage[] = [];

    actions.forEach((action) => {
      switch (action.type) {
        case "append":
          pendingMessages.push(action.message);
          break;
        case "setInitContent":
          setInitContent(action.content);
          break;
        case "setIsExecuting":
          setIsExecuting(action.value);
          break;
        case "setIsInitStatus":
          setIsInitStatus(action.value);
          break;
        case "notify":
          if (action.variant === "info") {
            toast.info(action.message);
          } else {
            toast.error(action.message);
          }
          break;
        default:
          break;
      }
    });

    if (pendingMessages.length > 0) {
      setMessages((prev) => [...prev, ...pendingMessages]);
    }
  }, []);

  const handleSessionId = (newSessionId: string | null) => {
    if (newSessionId !== sessionId) {
      settingsRequestSessionRef.current = null;
      setSessionId(newSessionId);
    }
  };

  const [initContent, setInitContent] = React.useState<InitMessageContent | null>(null);

  const messagesStartRef = React.useRef<HTMLDivElement>(null!);
  const messagesEndRef = React.useRef<HTMLDivElement>(null!);
  const wsRef = React.useRef<WebSocket | null>(null);
  const userInputRef = React.useRef<UserInputRef | null>(null);

  React.useEffect(() => {
    const mediaQuery = window.matchMedia("(max-width: 768px)");
    const handleMediaChange = (event: MediaQueryListEvent) => {
      setIsMobile(event.matches);
    };

    setIsMobile(mediaQuery.matches);
    mediaQuery.addEventListener("change", handleMediaChange);
    return () => mediaQuery.removeEventListener("change", handleMediaChange);
  }, []);

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
  }, [workingDirectory, gitAddress, directoryMode, apiKey, apiBaseUrl, permissionMode, envVars]);

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
      if (!sessionId) {
        return "";
      }
      const repoName = getRepositoryName(gitAddress);
      return repoName ? `${gitCodeSource}/${repoName}/${sessionId}` : "";
    }
    return workingDirectory;
  }, [directoryMode, getRepositoryName, gitAddress, sessionId, workingDirectory]);

  const openDrawer = (type: "chat" | "files") => {
    setDrawerContent(type);
    setIsDrawerOpen(true);
  };

  const handleSidebarToggle = (type: "chat" | "files") => {
    if (isMobile) {
      openDrawer(type);
      return;
    }

    //  if (type === "chat") {
    //     setShowChatHistory((prev) => !prev);
    //     return;
    //   }
    setShowChatHistory((prev) => !prev);
    setShowFileExplorer((prev) => !prev);
  };

  const loadFileContent = React.useCallback(
    async (filePath: string) => {
      setIsLoadingContent(true);
      setContentError(null);
      setDiffContentData(null);

      try {
        if (onlyDiff) {
          const diff = await getFileDiff(filePath);
          setDiffContentData(diff);
          setFileContent("");
          setSelectedFile(filePath);
        } else {
          const content = await readFile(filePath);
          setFileContent(content);
          setDiffContentData(null);
          setSelectedFile(filePath);
        }
      } catch (err) {
        console.error("Error loading file:", err);
        setContentError((err as Error).message);
        setFileContent("");
        setDiffContentData(null);
      } finally {
        setIsLoadingContent(false);
      }
    },
    [onlyDiff],
  );

  const handleOnFileDeleted = React.useCallback(
    (filePath: string) => {
      if (filePath === selectedFile) {
        setFileContent("");
        setDiffContentData(null);
      }
    },
    [selectedFile],
  );

  const handleOnLoadFileContent = React.useCallback(
    (filePath: string) => {
      loadFileContent(filePath);
    },
    [loadFileContent],
  );

  const handleOnFileReseted = React.useCallback(
    (filePath: string | null) => {
      if (selectedFile && selectedFile === filePath) {
        loadFileContent(selectedFile);
      }
    },
    [loadFileContent, selectedFile],
  );

  const handleOnFileSelected = React.useCallback(
    (filePath: string | null) => {
      if (filePath && filePath !== selectedFile) {
        setSelectedFile(filePath);
        loadFileContent(filePath);
        if (isMobile) {
          setIsDrawerOpen(false);
        }
      }
    },
    [isMobile, loadFileContent, selectedFile],
  );

  React.useEffect(() => {
    if (selectedFile) {
      loadFileContent(selectedFile);
    }
  }, [onlyDiff]);

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
        gitAddress: directoryMode === DirectoryMode.gitAddress ? gitAddress.trim() || null : null,
        apiKey: apiKey.trim() || null,
        apiBaseUrl: apiBaseUrl.trim() || null,
        systemPrompt: null,
        maxTurns: null,
        sessionId: sessionId,
        projectId: CLAUDE_CODE_PROJECT_ID,
        permissionMode: permissionMode,
        environmentVariables: buildEnvironmentVariables(),
      },
    };
  };

  const buildInputRequest = (message: AiMessage) => {
    return {
      type: 1,
      input: {
        input: toExecutionWsUserInput(message),
      },
    };
  };

  const buildInterruptRequest = (reason: string) => {
    return {
      type: 2,
      interrupt: {
        reason,
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

  const ensureSessionId = () => {
    if (sessionId) {
      return sessionId;
    }
    const newSessionId = Uuid4.generate().toCanonical();
    handleSessionId(newSessionId);
    return newSessionId;
  };

  const sendStatusRequest = (ws: WebSocket) => {
    // statusRequestPendingRef.current = true;
    const sessionId = ensureSessionId();
    sendSettingIfNeeded(ws, sessionId);
    setIsInitStatus(true);
    ws.send(JSON.stringify(buildInputRequest(createUserTextMessage("/status"))));
  };

  const setupWebSocket = () => {
    const protocol = window.location.protocol === "https:" ? "wss:" : "ws:";
    const wsUrl = `${protocol}//${window.location.host}/api/external-agents/claude-code/ws`;

    const ws = new WebSocket(wsUrl);
    wsRef.current = ws;

    ws.onopen = () => {
      // console.debug("WebSocket connected");
    };

    ws.onmessage = (event) => {
      try {
        const data: AiMessage = JSON.parse(event.data);
        console.debug("onmessage", data);

        applyAiMessageActions(handleAiMessage(data, { isInitStatus }));
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
      // console.debug("WebSocket closed:", event.code, event.reason);
      wsRef.current = null;
      settingsRequestSessionRef.current = null;
      setIsExecuting(false);

      if (event.code !== 1000) {
        console.error("WebSocket closed unexpectedly:", event.code, event.reason);
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
    // console.debug("comments", comments);

    let prompt = value.trim()
      ? value.trim()
      : "Please make modifications based on the following review comments";
    prompt += "\n\n";

    comments.forEach((comment) => {
      prompt += `file ${comment.filePath}, ${comment.isAfter ? "after" : "before"} the modification, the ${comment.lineIndex}th line: `;
      prompt += comment.content + "\n\n";
    });
    // console.debug("Final input with comments:", prompt);
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

      if (!ws || ws.readyState === WebSocket.CLOSED || ws.readyState === WebSocket.CLOSING) {
        ws = setupWebSocket();
        await waitForWebSocketOpen(ws);
      } else if (ws.readyState === WebSocket.CONNECTING) {
        await waitForWebSocketOpen(ws);
      }

      if (ws.readyState === WebSocket.OPEN) {
        const userMsg = createUserTextMessage(inputMsg);
        // Add user message to chat immediately
        setMessages((prev) => [...prev, userMsg]);

        const tid = ensureSessionId();
        sendSettingIfNeeded(ws, tid);
        const request = buildInputRequest(userMsg);
        // console.debug("Sending request:", request);
        ws.send(JSON.stringify(request));
      }
    } catch (error) {
      console.error("Execute failed:", error);
      toast.error(`Execute failed: ${error instanceof Error ? error.message : "Unknown error"}`);
      setIsExecuting(false);
    }
  };

  const sendInterruptToClaudeCode = () => {
    const ws = wsRef.current;

    if (!ws || ws.readyState !== WebSocket.OPEN) {
      toast.error("No active session to interrupt");
      setIsExecuting(false);
      return;
    }

    try {
      ws.send(JSON.stringify(buildInterruptRequest("Stop requested by user.")));
    } catch (error) {
      console.error("Failed to send interrupt request:", error);
      toast.error("Failed to interrupt execution");
    }
  };

  const handleClearSession = () => {
    setMessages([]);
  };

  const clearActiveSessionState = () => {
    setMessages([]);
    handleSessionId(null);
    if (wsRef.current) {
      wsRef.current.close(1000, "Session cleared");
      wsRef.current = null;
    }
    settingsRequestSessionRef.current = null;
  };

  const handleHistorySelect = async (sessionId: string) => {
    try {
      const details: ChatSessionRecordDetails | null = await getSessionBySessionId(
        sessionId,
        CLAUDE_CODE_PROJECT_ID,
      );
      if (!details) {
        return;
      }
      handleSessionSelect(details.messages ?? [], details.sessionId);
    } catch (error) {
      console.error("Failed to load session:", error);
    } finally {
      setIsDrawerOpen(false);
    }
  };

  const handleSessionSelect = (newMessages: AiMessage[], newSessionId: string) => {
    handleSessionId(newSessionId);
    for (let index = 0; index < newMessages.length; index++) {
      const aiMessage = newMessages[index];
      applyAiMessageActions(handleAiMessage(aiMessage, { isInitStatus }));
    }
    toast.info("load completed");
    // Close existing WebSocket to start fresh with loaded session
    if (wsRef.current) {
      wsRef.current.close(1000, "Session switched");
      wsRef.current = null;
    }
    settingsRequestSessionRef.current = null;
  };

  const handleSessionDeleted = (deletedSessionId: string) => {
    if (deletedSessionId !== sessionId) {
      return;
    }
    clearActiveSessionState();
  };

  const handleAllSessionsCleared = () => {
    if (!sessionId) {
      return;
    }
    clearActiveSessionState();
  };

  const handleNewChat = () => {
    void initializeNewChat();
    setIsDrawerOpen(false);
  };

  const initializeNewChat = async () => {
    handleClearSession();

    const newSessionId = Uuid4.generate().toCanonical();
    handleSessionId(newSessionId);

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
  const createArr = (key: string, value: string[] | undefined) => {
    return (
      <div className="grid grid-cols-3 items-center py-2 border-b">
        <Label>{key}</Label>
        <div className="col-span-2">
          {!value || value.length === 0
            ? "-"
            : value.map((item, i) => {
                return (
                  <Badge key={i} variant="outline">
                    {item}
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
        const callId = currentMsg.contents[0].additionalProperties?.callId as string;

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
              const resultCallId = msg.contents[0].additionalProperties?.callId as string;
              if (resultCallId === callId) {
                matchingResults.push({ msg, index: j });
              }
            }
          }

          // If we found matching results, create an accordion group
          if (matchingResults.length > 0) {
            const toolName =
              (currentMsg.contents[0].additionalProperties?.toolName as string) ?? "";
            const groupedMessages = [currentMsg, ...matchingResults.map((r) => r.msg)];

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
          currentMsg.contents[0].type === MessageContentType.FunctionResultContent;

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
    setIsDrawerOpen(false);
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
    messagesStartRef.current?.scrollIntoView({
      behavior: "smooth",
      block: "start",
    });
  };

  const isChatTab = currentTab === "chat";
  const isFilesTab = currentTab === "files";
  const activeSidebarVisible = isChatTab ? showChatHistory : showFileExplorer;
  const activeSidebarTitle = isChatTab ? "chat history" : "file explorer";

  return (
    <div className="relative flex flex-col h-[calc(100vh-58px)] w-full max-w-8xl mx-auto px-2 md:px-0 md:mr-2">
      <Tabs value={currentTab} onValueChange={handleTabChange} className="flex flex-col h-full">
        <div className="flex items-center gap-2">
          <TabsList className="w-fit">
            <TabsTrigger value="chat">Chat</TabsTrigger>
            <TabsTrigger value="files">Files</TabsTrigger>
          </TabsList>
          <div className="flex-1" />
          <div className="flex items-center gap-2">
            <Button
              variant="ghost"
              className="cursor-pointer"
              size="sm"
              onClick={() => handleSidebarToggle(isChatTab ? "chat" : "files")}
              title={
                activeSidebarVisible ? `Hide ${activeSidebarTitle}` : `Show ${activeSidebarTitle}`
              }
            >
              {activeSidebarVisible ? (
                <PanelLeftClose className="h-4 w-4" />
              ) : (
                <PanelLeftOpen className="h-4 w-4" />
              )}
            </Button>
          </div>
        </div>

        <div className="mt-2 flex-1 min-h-0">
          <ColResizeSplit>
            {!isMobile && isChatTab && showChatHistory && (
              <ColResizeSplit.Left minWidth={200} maxWidth={600}>
                <ChatHistoryList
                  currentSessionId={sessionId}
                  onSessionSelect={handleHistorySelect}
                  onNewChat={handleNewChat}
                  onSessionDeleted={handleSessionDeleted}
                  onAllSessionsCleared={handleAllSessionsCleared}
                />
              </ColResizeSplit.Left>
            )}
            {!isMobile && isFilesTab && showFileExplorer && (
              <ColResizeSplit.Left minWidth={200} maxWidth={600}>
                <Export
                  rootDirectory={resolvedWorkingDirectory}
                  onlyDiff={onlyDiff}
                  recursiveMode={recursiveMode}
                  onOnlyDiffChange={setOnlyDiff}
                  onFileDeleted={handleOnFileDeleted}
                  onFileSelected={handleOnFileSelected}
                  onFileReseted={handleOnFileReseted}
                  onLoadFileContent={handleOnLoadFileContent}
                />
              </ColResizeSplit.Left>
            )}
            <ColResizeSplit.Right>
              <div className="relative flex flex-1 min-h-0 overflow-hidden">
                <TabsContent value="chat" className="mt-0 h-full w-full">
                  <div className="flex flex-col h-full px-2 w-full">
                    <ChatSession
                      messages={messages}
                      messagesStartRef={messagesStartRef}
                      messagesEndRef={messagesEndRef}
                      processMessages={processMessages}
                    />
                  </div>
                </TabsContent>

                <TabsContent value="files" className="mt-0 h-full">
                  <div className="flex flex-col h-full px-2">
                    <div className="flex-1 min-h-0 pb-36">
                      {!selectedFile ? (
                        NoSelectedFile()
                      ) : (
                        <div className="flex flex-col h-full min-h-0">
                          <FileHeader file={selectedFile} />
                          <div className="flex-1 min-h-0 overflow-y-auto">
                            {isLoadingContent ? (
                              <FileLoading />
                            ) : contentError ? (
                              <FileError message={contentError} />
                            ) : onlyDiff && diffContentData ? (
                              diffContentData.unchanged ? (
                                <UnChangedFile
                                  diffContentData={diffContentData}
                                  selectedFile={selectedFile}
                                  comments={comments}
                                  setComments={setComments}
                                />
                              ) : (
                                <DiffViewer
                                  diff={diffContentData.diff}
                                  filePath={selectedFile}
                                  comments={comments}
                                  setComments={setComments}
                                />
                              )
                            ) : (
                              <FileViewer
                                content={fileContent}
                                filePath={selectedFile}
                                comments={comments}
                                setComments={setComments}
                                isDiffView={false}
                              />
                            )}
                          </div>
                        </div>
                      )}
                    </div>
                  </div>
                </TabsContent>

                <div className="absolute bottom-0 z-10 left-0 right-0 h-30 px-2 bg-linear-to-t from-bg-000 from-50% via-bg-000/80 via-70% to-transparent pointer-events-none">
                  <InputArea
                    isExecuting={isExecuting}
                    hasMessages={(messages?.length ?? 0) > 0}
                    onExecute={executeClaudeCode}
                    onExecuteWithComment={executeClaudeCodeWithComment}
                    onInterrupt={sendInterruptToClaudeCode}
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
              </div>
            </ColResizeSplit.Right>
          </ColResizeSplit>
        </div>
      </Tabs>

      <Drawer direction="left" open={isDrawerOpen} onOpenChange={setIsDrawerOpen}>
        <DrawerContent className="h-screen max-h-screen">
          <DrawerHeader>
            <DrawerTitle>
              {drawerContent === "files" ? "File Explorer" : "Chat History"}
            </DrawerTitle>
          </DrawerHeader>
          <div className="px-4 pb-6 h-full min-h-0 overflow-hidden">
            {drawerContent === "chat" && (
              <ChatHistoryList
                currentSessionId={sessionId}
                onSessionSelect={handleHistorySelect}
                onNewChat={handleNewChat}
                onSessionDeleted={handleSessionDeleted}
                onAllSessionsCleared={handleAllSessionsCleared}
              />
            )}
            {drawerContent === "files" && (
              <Export
                rootDirectory={resolvedWorkingDirectory}
                onlyDiff={onlyDiff}
                recursiveMode={recursiveMode}
                onOnlyDiffChange={setOnlyDiff}
                onFileDeleted={handleOnFileDeleted}
                onFileSelected={handleOnFileSelected}
                onFileReseted={handleOnFileReseted}
                onLoadFileContent={handleOnLoadFileContent}
              />
            )}
          </div>
        </DrawerContent>
      </Drawer>

      <Dialog open={showCommentDialog} onOpenChange={handleDialogClose}>
        <DialogContent showCloseButton={true}>
          <DialogHeader>
            <DialogTitle>Unsent Comments</DialogTitle>
            <DialogDescription>
              You have {comments.length} unsent comment(s). These will be cleared if you don&apos;t
              send them. Would you like to send them now?
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
