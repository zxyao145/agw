import {
  normalizeProfileName,
  normalizeServerUrl,
  parseEncodedConfig,
} from "@/features/servers/config-codec";

function encode(value: unknown): string {
  const bytes = new TextEncoder().encode(JSON.stringify(value));
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/u, "");
}

describe("server profile codec", () => {
  test("normalizes a root Server URL", () => {
    expect(normalizeServerUrl(" https://agw.example.com:8443/ ")).toBe(
      "https://agw.example.com:8443",
    );
  });

  test.each([
    "ftp://agw.example.com",
    "https://user:secret@agw.example.com",
    "https://agw.example.com/path",
    "https://agw.example.com/?token=x",
  ])("rejects unsafe or unsupported Server URL %s", (value) => {
    expect(() => normalizeServerUrl(value)).toThrow();
  });

  test("imports the existing Base64URL v2 contract", () => {
    expect(
      parseEncodedConfig(
        encode({
          version: 2,
          apiMajorVersion: 1,
          serverUrl: "http://192.168.1.10:30816/",
          token: "agw_mobile",
        }),
      ),
    ).toEqual({
      version: 2,
      apiMajorVersion: 1,
      serverUrl: "http://192.168.1.10:30816",
      token: "agw_mobile",
    });
  });

  test("uses the host when a profile name is omitted", () => {
    expect(normalizeProfileName("", "https://agw.example.com")).toBe("agw.example.com");
  });
});
