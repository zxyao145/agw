"use client";

import * as React from "react";
import { toast } from "sonner";
import { Settings, Send, ChevronDown } from "lucide-react";

import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";
import {
  Collapsible,
  CollapsibleContent,
  CollapsibleTrigger,
} from "@/components/ui/collapsible";
import { AiMessage, ClaudeCodeMessage, ResultMessage } from "./types";
import { cwd } from "process";



export default function ClaudeCodePage() {
  const [input, setInput] = React.useState("");
  const [workingDirectory, setWorkingDirectory] = React.useState("");
  const [apiKey, setApiKey] = React.useState("");
  const [isExecuting, setIsExecuting] = React.useState(false);
  const [messages, setMessages] = React.useState<AiMessage[]>([]);
  const [sessionId, setSessionId] = React.useState<string | null>(null);
  const [sessionInfo, setSessionInfo] = React.useState<{
    numTurns?: number;
    totalCostUsd?: number;
  } | null>(null);
  const [settingsOpen, setSettingsOpen] = React.useState(false);

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
    if (savedWorkingDir) setWorkingDirectory(savedWorkingDir);
    if (savedApiKey) setApiKey(savedApiKey);
  }, []);

  // Cleanup WebSocket on unmount
  React.useEffect(() => {
    return () => {
      if (wsRef.current && wsRef.current.readyState === WebSocket.OPEN) {
        wsRef.current.close(1000, "Component unmounted");
      }
    };
  }, []);

  const saveSettings = () => {
    localStorage.setItem("claudecode_workingDir", workingDirectory);
    localStorage.setItem("claudecode_apiKey", apiKey);
    setSettingsOpen(false);
    toast.success("Settings saved");
  };

  const setupWebSocket = () => {
    const protocol = window.location.protocol === "https:" ? "wss:" : "ws:";
    const wsUrl = `${protocol}//${window.location.host}/api/external-agents/claude-code/ws`;

    const ws = new WebSocket(wsUrl);
    wsRef.current = ws;

    ws.onopen = () => {
      console.log("WebSocket connected");
    };

    ws.onmessage = (event) => {
      try {
        const data: AiMessage = JSON.parse(event.data);
        console.log("onmessage", data)
        if (data.role === "system" && data.author == "init") {
          // [init]\ntype: system\nsubtype: init\ncwd: D:\source\repos\claude-code-sdk-csharp\nsession_id: 28f9ee8a-6e9a-4ce6-8573-20f611c1d909\ntools: .....
          const msgInfoArray = data.contents[0].content.split("\n");
          msgInfoArray.forEach((x) => {
            if (x.startsWith("session_id:")) {
              const sessionId = x.split(":")[1].trim();
              setSessionId(sessionId);
              return;
            }
          });
        } else if (data.role === "system" && data.author == "result") {
          setIsExecuting(false);
        } 
        if (
          data.role === "assistant"
          // || data.role === "user"
          || data.role === "system"
        ) {
          setMessages((prev) => [...prev, data]);
        } 
          // else if (message.type === "result") {
          //   const m : ResultMessage = JSON.parse(message.content)
          //   setSessionInfo({
          //     numTurns: m.numTurns,
          //     totalCostUsd: m.totalCostUsd,
          //   });

          //   if (m.isError) {
          //     toast.error(
          //       `Execution failed: ${m.errorMessage || "Unknown error"}`
          //     );
          //   } else {
          //     toast.success("Execution completed");
          //   }
          //   setIsExecuting(false);
          // } 
          // else if (data.type === "error") {
          //   // setMessages((prev) => [...prev, message]);
          //   // toast.error(`Error: ${message.errorMessage || message.content}`);
          //   // setIsExecuting(false);
          // }
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
      console.log("WebSocket closed:", event.code, event.reason);
      wsRef.current = null;
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

  const executeClaudeCode = async () => {
    if (!input.trim()) {
      toast.error("Please enter a prompt");
      return;
    }

    setIsExecuting(true);

    try {
      // Check if WebSocket exists and is open
      let ws = wsRef.current;

      if (!ws || ws.readyState === WebSocket.CLOSED || ws.readyState === WebSocket.CLOSING) {
        // Create new connection
        ws = setupWebSocket();

        // Wait for connection to open
        await new Promise<void>((resolve, reject) => {
          const onOpen = () => {
            ws!.removeEventListener('open', onOpen);
            ws!.removeEventListener('error', onError);
            resolve();
          };
          const onError = () => {
            ws!.removeEventListener('open', onOpen);
            ws!.removeEventListener('error', onError);
            reject(new Error("Failed to connect"));
          };
          ws!.addEventListener('open', onOpen);
          ws!.addEventListener('error', onError);
        });
      } else if (ws.readyState === WebSocket.CONNECTING) {
        // Wait for existing connection to open
        await new Promise<void>((resolve, reject) => {
          const onOpen = () => {
            ws!.removeEventListener('open', onOpen);
            ws!.removeEventListener('error', onError);
            resolve();
          };
          const onError = () => {
            ws!.removeEventListener('open', onOpen);
            ws!.removeEventListener('error', onError);
            reject(new Error("Failed to connect"));
          };
          ws!.addEventListener('open', onOpen);
          ws!.addEventListener('error', onError);
        });
      }

      // Send message
      if (ws.readyState === WebSocket.OPEN) {
        // Add user message to chat immediately
        setMessages((prev) => [
          ...prev,
          {
            messageId: "",
            author: "user",
            role: "user",
            contents: [
              {
                type: "TextContent",
                content: input,
              },
            ],
          },
        ]);

        const request = {
          input: input,
          workingDirectory: workingDirectory.trim() || null,
          apiKey: apiKey.trim() || null,
          systemPrompt: null,
          maxTurns: null,
          sessionId: sessionId,
        };

        ws.send(JSON.stringify(request));
        setInput(""); // Clear input after sending
      }
    } catch (error) {
      toast.error(`Execute failed: ${error instanceof Error ? error.message : "Unknown error"}`);
      setIsExecuting(false);
    }
  };

  const handleClearSession = () => {
    setMessages([]);
    setSessionInfo(null);
    setSessionId(null);
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
          <Dialog open={settingsOpen} onOpenChange={setSettingsOpen}>
            <DialogTrigger asChild>
              <Button variant="outline" size="sm">
                <Settings className="w-4 h-4 mr-2" />
                Settings
              </Button>
            </DialogTrigger>

            <DialogContent>
              <DialogHeader>
                <DialogTitle>ClaudeCode Settings</DialogTitle>
                <DialogDescription>
                  Configure working directory and API key
                </DialogDescription>
              </DialogHeader>

              <div className="grid gap-4 py-4">
                <div className="grid gap-2">
                  <Label htmlFor="settings-workingDir">
                    Working Directory (Optional)
                  </Label>
                  <Input
                    id="settings-workingDir"
                    value={workingDirectory}
                    onChange={(e) => setWorkingDirectory(e.target.value)}
                    placeholder="e.g., /path/to/project"
                  />
                  <p className="text-xs text-muted-foreground">
                    Directory where ClaudeCode will execute. Leave empty for current directory.
                  </p>
                </div>

                <div className="grid gap-2">
                  <Label htmlFor="settings-apiKey">
                    Anthropic API Key (Optional)
                  </Label>
                  <Input
                    id="settings-apiKey"
                    type="password"
                    value={apiKey}
                    onChange={(e) => setApiKey(e.target.value)}
                    placeholder="sk-ant-..."
                  />
                  <p className="text-xs text-muted-foreground">
                    Leave empty to use ANTHROPIC_AUTH_TOKEN environment variable.
                  </p>
                </div>
              </div>

              <DialogFooter>
                <DialogClose asChild>
                  <Button type="button" variant="outline">
                    Cancel
                  </Button>
                </DialogClose>
                <Button type="button" onClick={saveSettings}>
                  Save Settings
                </Button>
              </DialogFooter>
            </DialogContent>
          </Dialog>
        </div>
      </div>

      {/* Messages Area - Scrollable */}
      <div className="flex-1 overflow-y-auto p-4 space-y-4">
        {messages.length === 0 && (
          <div className="flex items-center justify-center h-full">
            <div className="text-center text-muted-foreground">
              <p className="text-lg mb-2">No messages yet</p>
              <p className="text-sm">Start a conversation by typing a message below</p>
            </div>
          </div>
        )}

        {messages.map((msg, index) => {
          const isUser = msg.role === "user";
          const isError = msg.role === "error";
          
          if (msg.role === "system" && msg.author === "init") {
            return (
              <div key={index} className="flex justify-start">
                <Collapsible
                  defaultOpen={false}
                  className="max-w-[80%] rounded-lg px-4 py-3 bg-yellow-100/50 border border-yellow-200 mr-12"
                >
                  <div className="flex items-center gap-2 mb-1">
                    <span className="text-xs font-semibold opacity-70">
                      {msg.author || msg.role}
                    </span>
                    <CollapsibleTrigger asChild>
                      <button className="ml-auto p-1 hover:bg-yellow-200/50 rounded transition-colors">
                        <ChevronDown className="w-4 h-4" />
                        <span className="sr-only">Toggle</span>
                      </button>
                    </CollapsibleTrigger>
                  </div>
                  <div className="text-xs text-muted-foreground italic mb-2">
                    Click to expand system message
                  </div>
                  <CollapsibleContent>
                    <div className="text-sm whitespace-pre-wrap break-words">
                      {msg.contents.map((content) => content.content).join('\n')}
                    </div>
                  </CollapsibleContent>
                </Collapsible>
              </div>
            );
          }

          return (
            <div
              key={index}
              className={`flex ${isUser ? "justify-end" : "justify-start"}`}
            >
              <div
                className={`max-w-[80%] rounded-lg px-4 py-3 ${
                  isUser
                    ? "bg-primary text-primary-foreground ml-12"
                    : isError
                      ? "bg-destructive/10 border border-destructive/20 mr-12"
                      : "bg-secondary mr-12"
                }`}
              >
                <div className="flex items-center gap-2 mb-1">
                  <span className="text-xs font-semibold opacity-70">
                    {isUser ? "You" : msg.author || msg.role}
                  </span>
                  {isError && (
                    <span className="text-xs font-semibold text-destructive">ERROR</span>
                  )}
                </div>
                <div className="text-sm whitespace-pre-wrap break-words">
                      {msg.contents.map((content) => content.content).join('\n')}
                </div>
              </div>
            </div>
          );
        })}

        {/* Session Info */}
        {sessionInfo && (
          <div className="flex justify-center">
            <div className="px-4 py-2 rounded-full bg-muted text-xs text-muted-foreground">
              Session completed • {sessionInfo.numTurns} turns
              {sessionInfo.totalCostUsd !== undefined && sessionInfo.totalCostUsd !== null && (
                <> • ${sessionInfo.totalCostUsd.toFixed(4)} USD</>
              )}
            </div>
          </div>
        )}

        <div ref={messagesEndRef} />
      </div>

      {/* Input Area - Fixed at Bottom */}
      <div className="border-t bg-background p-4">
        <div className="flex gap-2 items-end">
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
