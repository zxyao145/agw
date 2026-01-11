"use client";

import * as React from "react";
import { toast } from "sonner";
import { Send, Columns3Cog } from "lucide-react";
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from "@/components/ui/popover"
import { Badge } from "@/components/ui/badge"

import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";
import {
  Accordion,
  AccordionContent,
  AccordionItem,
  AccordionTrigger,
} from "@/components/ui/accordion";
import { AiMessage, ClaudeCodeMessageType, InitMessageContent, MessageContentType, PermissionMode } from "./types";
import { Ulid } from "id128";
import { AiMessageComponment } from "./components/message";
import { SettingsDialog } from "./components/settings-dialog";

export default function ClaudeCodePage() {
  const [input, setInput] = React.useState("");
  const [workingDirectory, setWorkingDirectory] = React.useState("");
  const [apiKey, setApiKey] = React.useState("");
  const [apiBaseUrl, setApiBaseUrl] = React.useState("");
  const [permissionMode, setPermissionMode] = React.useState<string>(PermissionMode.default);
  const [isExecuting, setIsExecuting] = React.useState(false);
  const [messages, setMessages] = React.useState<AiMessage[]>([]);
  const [threadId, setThreadId] = React.useState<string | null>(null);

  const [initContent, setInitContent] =
    React.useState<InitMessageContent | null>(null);

  const messagesEndRef = React.useRef<HTMLDivElement>(null);
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
    const savedPermissionMode = localStorage.getItem("claudecode_permissionMode");
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
          event.reason
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
        `Execute failed: ${error instanceof Error ? error.message : "Unknown error"}`
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

  // Process messages to identify FunctionCall + FunctionResult pairs
  const processMessages = (msgs: AiMessage[]) => {
    const items: Array<
      | { type: "accordion"; messages: AiMessage[]; toolName: string }
      | { type: "normal"; message: AiMessage }
    > = [];
    let i = 0;

    while (i < msgs.length) {
      const currentMsg = msgs[i];
      const nextMsg = msgs[i + 1];

      let shouldMerge = false;
      if (currentMsg &&  currentMsg.contents && nextMsg && nextMsg.contents) {
        const currentMsgCallId = currentMsg.contents[0].additionalProperties
          ?.callId as string;
        const nextMsgCallId = nextMsg.contents[0].additionalProperties
          ?.callId as string;

        shouldMerge =
          currentMsg.contents.length === 1 &&
          nextMsg.contents.length === 1 &&
          !! currentMsgCallId &&
          !! nextMsgCallId &&
          currentMsgCallId === nextMsgCallId;
        // currentMsg.contents[0].type === MessageContentType.FunctionCallContent &&
        // nextMsg.contents[0].type === MessageContentType.FunctionResultContent;
      }

      
      if (shouldMerge) {
        // Extract tool name from "Tool use: toolName(...)"
        const callContent = currentMsg.contents[0].content;
        // const match = callContent.match(/Tool use:\s*(\w+)/);
        const toolName =  callContent;

        items.push({
          type: "accordion",
          messages: [currentMsg, nextMsg],
          toolName,
        });
        i += 2; // Skip both messages
      } else {
        items.push({
          type: "normal",
          message: currentMsg,
        });
        i += 1;
      }
    }

    return items;
  };

  return (
    <div className="flex flex-col h-[calc(100vh-8rem)] w-full max-w-5xl mx-auto">
      {/* Header with Settings Button */}
      <div className="flex items-center justify-between p-4 border-b">
        <div>
          <h1 className="text-xl font-semibold">ClaudeCode Chat</h1>
          <p className="text-sm text-muted-foreground">
            Powered by WebSocket streaming
          </p>
        </div>

        <div className="flex gap-2">
          <SettingsDialog
            workingDirectory={workingDirectory}
            setWorkingDirectory={setWorkingDirectory}
            apiKey={apiKey}
            setApiKey={setApiKey}
            apiBaseUrl={apiBaseUrl}
            setApiBaseUrl={setApiBaseUrl}
            permissionMode={permissionMode}
            setPermissionMode={setPermissionMode}
          />
        </div>
      </div>

      {/* Messages Area - Scrollable */}
      <div className="flex-1 overflow-y-auto p-4 space-y-4">
        {messages.length === 0 && (
          <div className="flex items-center justify-center h-full">
            <div className="text-center text-muted-foreground">
              <p className="text-lg mb-2">No messages yet</p>
              <p className="text-sm">
                Start a conversation by typing a message below
              </p>
            </div>
          </div>
        )}

        {processMessages(messages).map((item, index) => {
          if (item.type === "accordion") {
            return (
              <Accordion key={index} type="single" collapsible className="w-full">
                <AccordionItem value="item-1" className="border rounded-lg px-2 last:border-b">
                  <AccordionTrigger>
                    <div className="flex items-center gap-2">
                      <Badge variant="secondary" className="text-xs">
                        {item.toolName}
                      </Badge>
                    </div>
                  </AccordionTrigger>
                  <AccordionContent>
                    <div className="space-y-4">
                      {item.messages.map((msg, msgIndex) => (
                        <AiMessageComponment key={msgIndex} message={msg} />
                      ))}
                    </div>
                  </AccordionContent>
                </AccordionItem>
              </Accordion>
            );
          } else {
            return <AiMessageComponment key={index} message={item.message} />;
          }
        })}
        
        <div ref={messagesEndRef} />
      </div>

      {/* Input Area - Fixed at Bottom */}
      <div className="border-t bg-background p-4">
        <div className="flex gap-2 items-end">
          <Popover>
            <PopoverTrigger asChild>
              <Button disabled={!initContent} variant="outline">
                <Columns3Cog />
              </Button>
            </PopoverTrigger>
            <PopoverContent className="w-120">
              <div className="grid gap-4">
                <div className="space-y-2">
                  <h4 className="leading-none font-medium">Claude Code Info</h4>
                  <p className="text-muted-foreground text-sm">
                    Claude Code meta info
                  </p>
                </div>
                {!initContent ? (
                  <p className="text-muted-foreground text-sm">
                    not interactive
                  </p>
                ) : (
                  <div className="grid max-h-80 overflow-auto">
                    <div className="grid grid-cols-3 items-center py-2 border-b">
                      <Label>claudeCodeVersion</Label>
                      <div className="col-span-2">
                        {initContent?.claudeCodeVersion ?? "-"}
                      </div>
                    </div>
                    <div className="grid grid-cols-3 items-center py-2 border-b">
                      <Label>permissionMode</Label>
                      <div className="col-span-2">
                        {initContent?.permissionMode ?? "-"}
                      </div>
                    </div>
                    <div className="grid grid-cols-3 items-center py-2 border-b">
                      <Label>model</Label>
                      <div className="col-span-2">
                        {initContent?.model ?? "-"}
                      </div>
                    </div>
                    {createArr("tools", initContent?.tools)}
                    {createArr("slashCommands", initContent?.slashCommands)}
                    {createArr("agents", initContent?.agents)}
                    {createArr("plugins", initContent?.plugins)}
                    {createArr("mcpServers", initContent?.mcpServers)}
                  </div>
                )}
              </div>
            </PopoverContent>
          </Popover>

          <Textarea
            value={input}
            onChange={(e) => setInput(e.target.value)}
            onKeyDown={handleKeyDown}
            placeholder="Type your message... (Shift+Enter for new line)"
            rows={3}
            className="flex-1 resize-none"
            disabled={isExecuting}
          />
          <Button
            onClick={executeClaudeCode}
            disabled={!input.trim() || isExecuting}
            size="lg"
          >
            <Send className="w-5 h-5" />
          </Button>
          {messages.length > 0 && (
            <Button
              variant="outline"
              size="lg"
              onClick={handleClearSession}
              disabled={isExecuting}
            >
              Clear Chat
            </Button>
          )}
        </div>
        <p className="text-xs text-muted-foreground mt-2">
          Press Enter to send • Shift+Enter for new line
        </p>
      </div>
    </div>
  );
}
