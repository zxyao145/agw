"use client";

import * as React from "react";

type ClaudeCodeMessage = {
  type: string;
  content: string;
  model?: string;
  numTurns?: number;
  totalCostUsd?: number;
  isError: boolean;
  errorMessage?: string;
};

export default function ClaudeCodeWebSocketPage() {
  const [input, setInput] = React.useState("");
  const [workingDirectory, setWorkingDirectory] = React.useState("");
  const [apiKey, setApiKey] = React.useState("");
  const [isConnected, setIsConnected] = React.useState(false);
  const [isExecuting, setIsExecuting] = React.useState(false);
  const [messages, setMessages] = React.useState<ClaudeCodeMessage[]>([]);
  const [sessionInfo, setSessionInfo] = React.useState<{
    numTurns?: number;
    totalCostUsd?: number;
  } | null>(null);
  const [connectionStatus, setConnectionStatus] = React.useState<string>("Disconnected");

  const wsRef = React.useRef<WebSocket | null>(null);

  const connectWebSocket = () => {
    if (wsRef.current?.readyState === WebSocket.OPEN) {
      return;
    }

    const protocol = window.location.protocol === "https:" ? "wss:" : "ws:";
    const wsUrl = `${protocol}//${window.location.host}/api/external-agents/claude-code/ws`;

    setConnectionStatus("Connecting...");

    const ws = new WebSocket(wsUrl);

    ws.onopen = () => {
      setIsConnected(true);
      setConnectionStatus("Connected");
      console.debug("WebSocket connected");
    };

    ws.onmessage = (event) => {
      try {
        const message: ClaudeCodeMessage = JSON.parse(event.data);

        if (message.type === "assistant" || message.type === "user" || message.type === "system") {
          setMessages((prev) => [...prev, message]);
        } else if (message.type === "result") {
          setSessionInfo({
            numTurns: message.numTurns,
            totalCostUsd: message.totalCostUsd,
          });
          setIsExecuting(false);
        } else if (message.type === "error") {
          setMessages((prev) => [...prev, message]);
          setIsExecuting(false);
        }
      } catch (e) {
        console.error("Parse error:", e);
      }
    };

    ws.onerror = (error) => {
      console.error("WebSocket error:", error);
      setConnectionStatus("Error");
      setIsConnected(false);
    };

    ws.onclose = (event) => {
      setIsConnected(false);
      setIsExecuting(false);
      setConnectionStatus(`Disconnected (${event.code})`);
      console.debug("WebSocket closed:", event.code, event.reason);
    };

    wsRef.current = ws;
  };

  const disconnectWebSocket = () => {
    if (wsRef.current) {
      wsRef.current.close(1000, "User initiated close");
      wsRef.current = null;
    }
  };

  const executeClaudeCode = () => {
    if (!wsRef.current || wsRef.current.readyState !== WebSocket.OPEN) {
      alert("WebSocket not connected. Please connect first.");
      return;
    }

    if (!input.trim()) {
      alert("Please enter a prompt");
      return;
    }

    setIsExecuting(true);
    setMessages([]);
    setSessionInfo(null);

    const request = {
      input: input,
      workingDirectory: workingDirectory.trim() || null,
      apiKey: apiKey.trim() || null,
      systemPrompt: null,
      maxTurns: null,
    };

    wsRef.current.send(JSON.stringify(request));
  };

  const handleClearSession = () => {
    setMessages([]);
    setSessionInfo(null);
    setInput("");
  };

  React.useEffect(() => {
    return () => {
      disconnectWebSocket();
    };
  }, []);

  return (
    <div className="min-h-screen bg-gray-50 p-8">
      <div className="max-w-4xl mx-auto space-y-6">
        {/* Header */}
        <div className="bg-white rounded-lg shadow p-6">
          <h1 className="text-2xl font-bold text-gray-900 mb-2">
            ClaudeCode WebSocket Client
          </h1>
          <p className="text-sm text-gray-600">
            Real-time communication with ClaudeCode via WebSocket
          </p>
        </div>

        {/* Connection Controls */}
        <div className="bg-white rounded-lg shadow p-6">
          <div className="flex items-center justify-between mb-4">
            <div>
              <h2 className="text-lg font-semibold text-gray-900">Connection</h2>
              <p className="text-sm text-gray-600">
                Status: <span className={`font-semibold ${isConnected ? 'text-green-600' : 'text-red-600'}`}>
                  {connectionStatus}
                </span>
              </p>
            </div>
            <div className="flex gap-2">
              <button
                onClick={connectWebSocket}
                disabled={isConnected}
                className="px-4 py-2 bg-blue-600 text-white rounded-md hover:bg-blue-700 disabled:bg-gray-400 disabled:cursor-not-allowed"
              >
                Connect
              </button>
              <button
                onClick={disconnectWebSocket}
                disabled={!isConnected}
                className="px-4 py-2 bg-red-600 text-white rounded-md hover:bg-red-700 disabled:bg-gray-400 disabled:cursor-not-allowed"
              >
                Disconnect
              </button>
            </div>
          </div>
        </div>

        {/* Input Form */}
        <div className="bg-white rounded-lg shadow p-6">
          <h2 className="text-lg font-semibold text-gray-900 mb-4">Execute Query</h2>

          <div className="space-y-4">
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">
                Prompt
              </label>
              <textarea
                value={input}
                onChange={(e) => setInput(e.target.value)}
                placeholder="Enter your prompt for ClaudeCode..."
                rows={4}
                className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
                disabled={isExecuting}
              />
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">
                Working Directory (Optional)
              </label>
              <input
                type="text"
                value={workingDirectory}
                onChange={(e) => setWorkingDirectory(e.target.value)}
                placeholder="e.g., /path/to/project"
                className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
                disabled={isExecuting}
              />
              <p className="text-xs text-gray-500 mt-1">
                Directory where ClaudeCode will execute. Leave empty to use current directory.
              </p>
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">
                Anthropic API Key (Optional)
              </label>
              <input
                type="password"
                value={apiKey}
                onChange={(e) => setApiKey(e.target.value)}
                placeholder="sk-ant-..."
                className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
                disabled={isExecuting}
              />
              <p className="text-xs text-gray-500 mt-1">
                Leave empty to use ANTHROPIC_AUTH_TOKEN environment variable.
              </p>
            </div>

            <div className="flex gap-2">
              <button
                onClick={executeClaudeCode}
                disabled={!input.trim() || isExecuting || !isConnected}
                className="flex-1 px-4 py-2 bg-green-600 text-white rounded-md hover:bg-green-700 disabled:bg-gray-400 disabled:cursor-not-allowed"
              >
                {isExecuting ? "Executing..." : "Execute"}
              </button>

              {messages.length > 0 && (
                <button
                  onClick={handleClearSession}
                  disabled={isExecuting}
                  className="px-4 py-2 bg-gray-600 text-white rounded-md hover:bg-gray-700 disabled:bg-gray-400 disabled:cursor-not-allowed"
                >
                  Clear
                </button>
              )}
            </div>
          </div>
        </div>

        {/* Response Display */}
        {messages.length > 0 && (
          <div className="bg-white rounded-lg shadow p-6">
            <h2 className="text-lg font-semibold text-gray-900 mb-4">Response</h2>

            <div className="space-y-3">
              {messages.map((msg, index) => {
                // Determine background color based on message type
                let bgClass = "bg-blue-50 border border-blue-200"; // default for assistant
                if (msg.type === "error" || msg.isError) {
                  bgClass = "bg-red-50 border border-red-200";
                } else if (msg.type === "user") {
                  bgClass = "bg-green-50 border border-green-200";
                } else if (msg.type === "system") {
                  bgClass = "bg-yellow-50 border border-yellow-200";
                } else if (msg.type === "assistant") {
                  bgClass = "bg-blue-50 border border-blue-200";
                }

                return (
                  <div
                    key={index}
                    className={`p-4 rounded-md ${bgClass}`}
                  >
                    <div className="flex items-center gap-2 mb-2">
                      <span className="text-xs font-semibold text-gray-600 uppercase">
                        {msg.model || msg.type}
                      </span>
                      {msg.isError && (
                        <span className="text-xs font-semibold text-red-600">ERROR</span>
                      )}
                    </div>
                    <div className="text-sm text-gray-900 whitespace-pre-wrap font-mono">
                      {msg.content || msg.errorMessage}
                    </div>
                  </div>
                );
              })}

              {sessionInfo && (
                <div className="p-4 rounded-md bg-gray-100 border border-gray-300">
                  <div className="text-xs text-gray-700 font-mono">
                    Session completed • {sessionInfo.numTurns} turns
                    {sessionInfo.totalCostUsd !== undefined && sessionInfo.totalCostUsd !== null && (
                      <> • ${sessionInfo.totalCostUsd.toFixed(4)} USD</>
                    )}
                  </div>
                </div>
              )}
            </div>
          </div>
        )}

        {/* Debug Info */}
        <div className="bg-white rounded-lg shadow p-6">
          <h2 className="text-lg font-semibold text-gray-900 mb-2">Debug Info</h2>
          <div className="text-xs text-gray-600 font-mono space-y-1">
            <div>WebSocket URL: ws://{window.location.host}/api/external-agents/claude-code/ws</div>
            <div>Ready State: {wsRef.current?.readyState ?? "null"} (0=CONNECTING, 1=OPEN, 2=CLOSING, 3=CLOSED)</div>
            <div>Messages Received: {messages.length}</div>
            <div>Is Executing: {isExecuting ? "Yes" : "No"}</div>
          </div>
        </div>
      </div>
    </div>
  );
}
