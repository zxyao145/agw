import {
  createAgwApiClient,
  AgwApiError,
  verifyServerCompatibility,
} from "../src/rn/api/agw-api-client";

const config = {
  version: 2 as const,
  apiMajorVersion: 1 as const,
  serverUrl: "https://api.example.com/root",
  token: "mobile-key",
};

const fetchMock = jest.fn();

describe("createAgwApiClient", () => {
  beforeEach(() => {
    fetchMock.mockReset();
    globalThis.fetch = fetchMock as unknown as typeof fetch;
  });

  it("sends authenticated GET requests and unwraps Bens.Results data", async () => {
    fetchMock.mockResolvedValueOnce(
      jsonResponse({
        code: 2000000,
        title: "OK",
        data: [{ id: "project-1", name: "Mobile Project" }],
      }),
    );

    const client = createAgwApiClient(config);
    const result = await client.getJson("/api/projects", {
      query: { enabled: true, skip: undefined },
    });

    expect(result).toEqual([{ id: "project-1", name: "Mobile Project" }]);
    expect(fetchMock).toHaveBeenCalledWith(
      "https://api.example.com/root/api/projects?enabled=true",
      {
        headers: { Authorization: "Bearer mobile-key" },
        method: "GET",
      },
    );
  });

  it("builds query strings without requiring URLSearchParams", async () => {
    const originalUrlSearchParams = globalThis.URLSearchParams;
    Object.defineProperty(globalThis, "URLSearchParams", {
      configurable: true,
      value: undefined,
    });

    fetchMock.mockResolvedValueOnce(
      jsonResponse({
        code: 2000000,
        title: "OK",
        data: { items: [] },
      }),
    );

    try {
      const client = createAgwApiClient(config);
      await client.getJson("/api/files/list", {
        query: { path: "D:\\work\\mobile", diff: false },
      });
    } finally {
      Object.defineProperty(globalThis, "URLSearchParams", {
        configurable: true,
        value: originalUrlSearchParams,
      });
    }

    expect(fetchMock).toHaveBeenCalledWith(
      "https://api.example.com/root/api/files/list?path=D%3A%5Cwork%5Cmobile&diff=false",
      expect.objectContaining({ method: "GET" }),
    );
  });

  it("keeps configured localhost server domains unchanged on Android", async () => {
    fetchMock.mockResolvedValueOnce(
      jsonResponse({
        code: 2000000,
        title: "OK",
        data: [],
      }),
    );

    const client = createAgwApiClient(
      {
        version: 2,
        apiMajorVersion: 1 as const,
        serverUrl: "http://localhost:5015",
        token: "mobile-key",
      },
      { platform: "android" },
    );
    await client.getJson("/api/projects");

    expect(fetchMock).toHaveBeenCalledWith("http://localhost:5015/api/projects", {
      headers: { Authorization: "Bearer mobile-key" },
      method: "GET",
    });
  });

  it("serializes JSON POST bodies", async () => {
    fetchMock.mockResolvedValueOnce(
      jsonResponse({
        code: 2000000,
        title: "OK",
        data: { taskId: "task-1", messages: [] },
      }),
    );

    const client = createAgwApiClient(config);
    await client.postJson("/api/executions/agent-1/execute", {
      agentType: 0,
      input: "hello",
    });

    expect(fetchMock).toHaveBeenCalledWith(
      "https://api.example.com/root/api/executions/agent-1/execute",
      {
        body: JSON.stringify({ agentType: 0, input: "hello" }),
        headers: {
          "Content-Type": "application/json",
          Authorization: "Bearer mobile-key",
        },
        method: "POST",
      },
    );
  });

  it("returns raw text responses", async () => {
    fetchMock.mockResolvedValueOnce(textResponse("file contents", "text/plain; charset=utf-8"));

    const client = createAgwApiClient(config);

    await expect(client.getText("/api/files/read")).resolves.toBe("file contents");
  });

  it("throws AgwApiError for failed responses", async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({ message: "Authentication is required." }, 401));

    const client = createAgwApiClient(config);

    await expect(client.getJson("/api/projects")).rejects.toMatchObject({
      name: "AgwApiError",
      status: 401,
      body: { message: "Authentication is required." },
    });
    expect(AgwApiError).toBeDefined();
  });

  it("rejects a server with a different API major", async () => {
    fetchMock.mockResolvedValueOnce(
      jsonResponse({ code: 0, title: "OK", data: { apiMajorVersion: 2 } }),
    );

    await expect(verifyServerCompatibility(config)).rejects.toThrow("client requires 1");
  });
});

function jsonResponse(body: unknown, status = 200): Response {
  return {
    ok: status >= 200 && status < 300,
    status,
    statusText: status === 200 ? "OK" : "Unauthorized",
    headers: {
      get: (name: string) => (name.toLowerCase() === "content-type" ? "application/json" : null),
    },
    json: async () => body,
    text: async () => JSON.stringify(body),
  } as unknown as Response;
}

function textResponse(body: string, contentType: string, status = 200): Response {
  return {
    ok: status >= 200 && status < 300,
    status,
    statusText: status === 200 ? "OK" : "Error",
    headers: {
      get: (name: string) => (name.toLowerCase() === "content-type" ? contentType : null),
    },
    json: async () => {
      throw new Error("not json");
    },
    text: async () => body,
  } as unknown as Response;
}
