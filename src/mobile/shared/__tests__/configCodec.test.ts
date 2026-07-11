import {
  AgwConfigError,
  encodeConfigBase64Url,
  parseConfigFileContent,
  parseEncodedConfig,
} from "../src/rn/config/agw-config";

describe("Agw local config codec", () => {
  it("round-trips a normalized config through Base64URL", () => {
    const encoded = encodeConfigBase64Url({
      version: 2,
      apiMajorVersion: 1 as const,
      serverUrl: "https://api.example.com/",
      token: " secret-key ",
    });

    expect(encoded).not.toMatch(/[+/=]/);
    expect(parseEncodedConfig(encoded)).toEqual({
      version: 2,
      apiMajorVersion: 1 as const,
      serverUrl: "https://api.example.com",
      token: "secret-key",
    });
  });

  it("rejects legacy alias fields", () => {
    expect(() =>
      parseConfigFileContent(
        JSON.stringify({ version: 1, domain: "http://localhost:5015/", api_key: "local-key" }),
      ),
    ).toThrow(AgwConfigError);
  });

  it("rejects invalid server domains", () => {
    expect(() =>
      parseConfigFileContent(
        JSON.stringify({
          version: 2,
          apiMajorVersion: 1 as const,
          serverUrl: "ftp://api.example.com",
          token: "key",
        }),
      ),
    ).toThrow(AgwConfigError);
  });

  it("rejects server URLs mounted below a subpath", () => {
    expect(() =>
      parseConfigFileContent(
        JSON.stringify({
          version: 2,
          apiMajorVersion: 1 as const,
          serverUrl: "https://api.example.com/agw",
          token: "key",
        }),
      ),
    ).toThrow(AgwConfigError);
  });

  it("ignores whitespace in Base64URL payloads", () => {
    const encoded = encodeConfigBase64Url({
      version: 2,
      apiMajorVersion: 1 as const,
      serverUrl: "https://api.example.com",
      token: "key",
    });
    const wrapped = `${encoded.slice(0, 8)}\n${encoded.slice(8)}`;

    expect(parseEncodedConfig(wrapped).token).toBe("key");
  });

  it("rejects standard Base64 payloads", () => {
    const standardBase64 = `${encodeConfigBase64Url({
      version: 2,
      apiMajorVersion: 1 as const,
      serverUrl: "https://api.example.com",
      token: "key",
    })}=`;

    expect(() => parseEncodedConfig(standardBase64)).toThrow(AgwConfigError);
  });
});
