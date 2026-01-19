"use client";

import * as React from "react";
import { toast } from "sonner";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import {
  ClaudeCodeMessageType,
  InitMessageContent,
  PermissionMode,
} from "./types";

import { AiMessage, AiMessageContent, MessageContentType } from "@/types";

import { Ulid } from "id128";
import { FileExplorer } from "./components/file-explorer";
import { Chat } from "./components/chat";
import { Badge } from "@/components/ui/badge";
import { Label } from "@/components/ui/label";
import "./page.css";

export default function ClaudeCodePage() {
  const [input, setInput] = React.useState("");
  const [workingDirectory, setWorkingDirectory] = React.useState("");
  const [apiKey, setApiKey] = React.useState("");
  const [apiBaseUrl, setApiBaseUrl] = React.useState("");
  const [permissionMode, setPermissionMode] = React.useState<string>(
    PermissionMode.default,
  );
  const [isExecuting, setIsExecuting] = React.useState(false);
  const [messages, setMessages] = React.useState<AiMessage[]>([]);
  const [threadId, setThreadId] = React.useState<string | null>(null);

  const [initContent, setInitContent] =
    React.useState<InitMessageContent | null>(null);

  const messagesEndRef = React.useRef<HTMLDivElement>(null!);
  const wsRef = React.useRef<WebSocket | null>(null);

  // Auto-scroll to bottom when new messages arrive
  React.useEffect(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [messages]);

  // Load settings from localStorage on mount
  React.useEffect(() => {
    const savedWorkingDir = localStorage.getItem("claudecode_workingDir");
    const savedApiKey = localStorage.getItem("claudecode_apiKey");
    const savedApiBaseUrl = localStorage.getItem("claudecode_apiBaseUrl");
    const savedPermissionMode = localStorage.getItem(
      "claudecode_permissionMode",
    );
    if (savedWorkingDir) setWorkingDirectory(savedWorkingDir);
    if (savedApiKey) setApiKey(savedApiKey);
    if (savedApiBaseUrl) setApiBaseUrl(savedApiBaseUrl);
    if (savedPermissionMode) setPermissionMode(savedPermissionMode);
  }, []);

  // Cleanup WebSocket on unmount
  React.useEffect(() => {
    return () => {
      if (wsRef.current && wsRef.current.readyState === WebSocket.OPEN) {
        wsRef.current.close(1000, "Component unmounted");
      }
    };
  }, []);

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
        console.debug("onmessage", data);

        if (data.role === "system") {
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

          if (
            data.additionalProperties!.type === "system" &&
            data.additionalProperties!.subtype === "init"
          ) {
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
          } else if (data.additionalProperties!.type === "result") {
            setIsExecuting(false);
            setMessages((prev) => [...prev, data]);
          } else {
            setMessages((prev) => [...prev, data]);
          }
        } else if (data.role === "assistant") {
          setMessages((prev) => [...prev, data]);
        } else if (data.role === "user") {
          setMessages((prev) => [...prev, data]);
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

  const executeClaudeCode = async () => {
    if (!input.trim()) {
      toast.error("Please enter a prompt");
      return;
    }

    setIsExecuting(true);

    try {
      // Check if WebSocket exists and is open
      let ws = wsRef.current;

      if (
        !ws ||
        ws.readyState === WebSocket.CLOSED ||
        ws.readyState === WebSocket.CLOSING
      ) {
        // Create new connection
        ws = setupWebSocket();

        // Wait for connection to open
        await new Promise<void>((resolve, reject) => {
          const onOpen = () => {
            ws!.removeEventListener("open", onOpen);
            ws!.removeEventListener("error", onError);
            resolve();
          };
          const onError = () => {
            ws!.removeEventListener("open", onOpen);
            ws!.removeEventListener("error", onError);
            reject(new Error("Failed to connect"));
          };
          ws!.addEventListener("open", onOpen);
          ws!.addEventListener("error", onError);
        });
      } else if (ws.readyState === WebSocket.CONNECTING) {
        // Wait for existing connection to open
        await new Promise<void>((resolve, reject) => {
          const onOpen = () => {
            ws!.removeEventListener("open", onOpen);
            ws!.removeEventListener("error", onError);
            resolve();
          };
          const onError = () => {
            ws!.removeEventListener("open", onOpen);
            ws!.removeEventListener("error", onError);
            reject(new Error("Failed to connect"));
          };
          ws!.addEventListener("open", onOpen);
          ws!.addEventListener("error", onError);
        });
      }

      // Send message
      if (ws.readyState === WebSocket.OPEN) {
        const userMsg: AiMessage = {
          messageId: "",
          author: "user",
          role: "user",
          contents: [
            {
              type: MessageContentType.TextContent,
              content: input,
            },
          ],
        };
        // Add user message to chat immediately
        setMessages((prev) => [...prev, userMsg]);

        let tid;
        if (threadId) {
          tid = threadId;
        } else {
          tid = Ulid.generate().toCanonical();
          setThreadId(tid);
        }

        const request = {
          input: input,
          workingDirectory: workingDirectory.trim() || null,
          apiKey: apiKey.trim() || null,
          apiBaseUrl: apiBaseUrl.trim() || null,
          systemPrompt: null,
          maxTurns: null,
          threadId: tid,
          permissionMode: permissionMode,
        };

        ws.send(JSON.stringify(request));
        setInput(""); // Clear input after sending
      }
    } catch (error) {
      toast.error(
        `Execute failed: ${error instanceof Error ? error.message : "Unknown error"}`,
      );
      setIsExecuting(false);
    }
  };

  const handleClearSession = () => {
    setMessages([]);
    setThreadId(null);
    // Close WebSocket to start fresh session on next message
    if (wsRef.current) {
      wsRef.current.close(1000, "Session cleared");
      wsRef.current = null;
    }
  };

  const handleKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === "Enter" && !e.shiftKey) {
      e.preventDefault();
      executeClaudeCode();
    }
  };

  const createArr = (key: string, value: string[]) => {
    return (
      <div className="grid grid-cols-3 items-center py-2 border-b">
        <Label>{key}</Label>
        <div className="col-span-2">
          {!value
            ? "-"
            : value.map((item) => <Badge variant="outline">{item}</Badge>)}
        </div>
      </div>
    );
  };

  // Process messages to identify FunctionCall + FunctionResult(s) groups by callId
  const processMessages = (msgs: AiMessage[]) => {
    const items: Array<
      | { type: "accordion"; messages: AiMessage[]; toolName: string }
      | { type: "normal"; message: AiMessage }
    > = [];

    // Track which message indices have been processed
    const processedIndices = new Set<number>();

    for (let i = 0; i < msgs.length; i++) {
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
            const toolName = currentMsg.contents[0].content;
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

  return (
    <div className="flex flex-col h-[calc(100vh-52px)] w-full max-w-8xl mx-auto mr-2">
      {/* Header with Tabs */}
      <div className="border-b mb-2 h-full">
        <Tabs defaultValue="code" className="w-full h-full flex-col">
          <TabsList>
            <TabsTrigger value="code">Code</TabsTrigger>
            <TabsTrigger value="files">Files</TabsTrigger>
          </TabsList>

          <TabsContent value="code" className="mt-0 py-2">
            <Chat
              messages={messages}
              input={input}
              setInput={setInput}
              isExecuting={isExecuting}
              workingDirectory={workingDirectory}
              setWorkingDirectory={setWorkingDirectory}
              apiKey={apiKey}
              setApiKey={setApiKey}
              apiBaseUrl={apiBaseUrl}
              setApiBaseUrl={setApiBaseUrl}
              permissionMode={permissionMode}
              setPermissionMode={setPermissionMode}
              initContent={initContent}
              messagesEndRef={messagesEndRef}
              onExecute={executeClaudeCode}
              onClearSession={handleClearSession}
              onKeyDown={handleKeyDown}
              processMessages={processMessages}
              createArr={createArr}
            />
          </TabsContent>

          <TabsContent value="files" className="mt-0 py-2">
            <div className="flex flex-col min-h-[calc(100vh-96px)]">
              <FileExplorer
                rootDirectory={workingDirectory}
                className="h-full border-0 rounded-none"
              />
            </div>
          </TabsContent>
        </Tabs>
      </div>
    </div>
  );
}
