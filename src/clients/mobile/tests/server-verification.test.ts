import { verifyServerProfile } from "@/features/servers/server-verification";
import type { ServerProfile } from "@/features/servers/types";

const profile: ServerProfile = {
  id: "profile-1",
  name: "Remote",
  serverUrl: "https://agw.example.com",
  apiMajorVersion: 1,
  allowInsecureHttp: false,
};

describe("server verification", () => {
  test("requires a compatible initialized Server and authenticated Bearer session", async () => {
    const originalFetch = globalThis.fetch;
    const requests: Array<{ url: string; init?: RequestInit }> = [];
    globalThis.fetch = jest.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      requests.push({ url: String(input), init });
      const data = String(input).endsWith("/api/server-info")
        ? { serverVersion: "1.0.0", apiMajorVersion: 1, initialized: true }
        : { authenticated: true, accessMode: "bearer", apiMajorVersion: 1 };
      return Response.json({ code: 0, title: "OK", data });
    }) as typeof fetch;

    try {
      const verified = await verifyServerProfile(profile, "agw_mobile");
      expect(verified.profile.id).toBe(profile.id);
      expect(requests.map((request) => request.url)).toEqual([
        "https://agw.example.com/api/server-info",
        "https://agw.example.com/api/auth/session",
      ]);
      expect((requests[0].init?.headers as Record<string, string> | undefined)?.Authorization).toBe(
        "Bearer agw_mobile",
      );
      expect(requests[0].init?.signal).toBeInstanceOf(AbortSignal);
      expect(requests[1].init?.signal).toBe(requests[0].init?.signal);
    } finally {
      globalThis.fetch = originalFetch;
    }
  });

  test("normalizes native network errors", async () => {
    const originalFetch = globalThis.fetch;
    globalThis.fetch = jest.fn(async () => {
      throw new Error("fetch failed: UnexpectedException: Could not connect to the server.");
    }) as typeof fetch;

    try {
      await expect(verifyServerProfile(profile, "agw_mobile")).rejects.toThrow(
        "Could not connect to the Agw Server. Check the Server URL and network, then try again.",
      );
    } finally {
      globalThis.fetch = originalFetch;
    }
  });

  test("times out a stalled server connection after five seconds", async () => {
    const originalFetch = globalThis.fetch;
    jest.useFakeTimers();
    globalThis.fetch = jest.fn(
      (_input: RequestInfo | URL, init?: RequestInit) =>
        new Promise<Response>((_resolve, reject) => {
          init?.signal?.addEventListener("abort", () => reject(new Error("aborted")), {
            once: true,
          });
        }),
    ) as typeof fetch;

    try {
      const verification = verifyServerProfile(profile, "agw_mobile");
      await Promise.resolve();
      jest.advanceTimersByTime(4_999);
      await Promise.resolve();
      jest.advanceTimersByTime(1);

      await expect(verification).rejects.toThrow(
        "Server verification timed out after 5 seconds. Check the Server URL, network, firewall, and Server availability, then try again.",
      );
    } finally {
      globalThis.fetch = originalFetch;
      jest.useRealTimers();
    }
  });

  test("does not classify response processing errors as connection failures", async () => {
    const originalFetch = globalThis.fetch;
    globalThis.fetch = jest.fn(async () => {
      return {
        headers: {
          get: () => {
            throw new Error("response parsing failed");
          },
        },
      } as unknown as Response;
    }) as typeof fetch;

    try {
      await expect(verifyServerProfile(profile, "agw_mobile")).rejects.toThrow(
        "response parsing failed",
      );
    } finally {
      globalThis.fetch = originalFetch;
    }
  });

  test("rejects a successful response with an invalid payload", async () => {
    const originalFetch = globalThis.fetch;
    globalThis.fetch = jest.fn(async () => Response.json({ code: 0, title: "OK" })) as typeof fetch;

    try {
      await expect(verifyServerProfile(profile, "agw_mobile")).rejects.toThrow(
        "The Agw Server returned an invalid response.",
      );
    } finally {
      globalThis.fetch = originalFetch;
    }
  });

  test("blocks HTTP before the user confirms the warning", async () => {
    await expect(
      verifyServerProfile({ ...profile, serverUrl: "http://agw.example.com" }, "agw_mobile"),
    ).rejects.toThrow("Confirm the HTTP security warning");
  });
});
