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
