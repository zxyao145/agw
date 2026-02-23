export type ExecutionWsRequest = {
  agentType: number;
  input: string;
  sessionId?: string | null;
  projectId?: string | null;
};

function toWsOrigin(baseUrl: string): string {
  const url = new URL(baseUrl, window.location.origin);
  if (url.protocol === "https:") {
    url.protocol = "wss:";
  } else if (url.protocol === "http:") {
    url.protocol = "ws:";
  }
  return `${url.protocol}//${url.host}`;
}

function buildExecutionWsUrls(id: string): string[] {
  
  const protocol = window.location.protocol === "https:" ? "wss:" : "ws:";
  const url = `${protocol}//${window.location.host}/api/executions/${id}/execute-ws`;
  const urls: string[] = [];
  urls.push(url);
  return urls;

  const sameOriginWs = `${toWsOrigin(window.location.origin)}/api/executions/${id}/execute-ws`;
  urls.push(sameOriginWs);

  const publicApiBase = process.env.NEXT_PUBLIC_API_BASE_URL?.trim();
  if (publicApiBase) {
    const fromEnv = `${toWsOrigin(publicApiBase)}/api/executions/${id}/execute-ws`;
    if (!urls.includes(fromEnv)) {
      urls.push(fromEnv);
    }
  }

  // Local dev fallback: Next dev server often runs on 3000 while backend runs on 5015.
  if (window.location.hostname === "localhost" && window.location.port === "3000") {
    const localBackend = `ws://localhost:5015/api/executions/${id}/execute-ws`;
    if (!urls.includes(localBackend)) {
      urls.push(localBackend);
    }
  }

  return urls;
}

function openExecutionWebSocket(
  wsUrl: string,
  request: ExecutionWsRequest,
  onMessage: (data: string) => void
): Promise<void> {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(wsUrl);
    let settled = false;

    const fail = (message: string) => {
      if (settled) return;
      settled = true;
      reject(new Error(message));
    };

    ws.onopen = () => {
      ws.send(JSON.stringify(request));
    };

    ws.onmessage = (event) => {
      if (typeof event.data === "string") {
        onMessage(event.data);
      }
    };

    ws.onerror = () => {
      fail("WebSocket connection error");
    };

    ws.onclose = (event) => {
      if (settled) return;
      settled = true;
      if (event.code === 1000) {
        resolve();
        return;
      }
      reject(
        new Error(
          event.reason || `WebSocket closed unexpectedly with code ${event.code}`
        )
      );
    };
  });
}

export async function executeWithWebSocket(
  id: string,
  request: ExecutionWsRequest,
  onMessage: (data: string) => void
): Promise<void> {
  const urls = buildExecutionWsUrls(id);
  let lastError: Error | null = null;

  for (const url of urls) {
    try {
      await openExecutionWebSocket(url, request, onMessage);
      return;
    } catch (error) {
      lastError =
        error instanceof Error ? error : new Error("WebSocket connection error");
    }
  }

  throw lastError ?? new Error("WebSocket connection error");
}
