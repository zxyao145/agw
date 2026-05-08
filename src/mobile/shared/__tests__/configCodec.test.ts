import {
  AgwConfigError,
  encodeConfigBase64Url,
  parseConfigFileContent,
  parseEncodedConfig,
} from "../src/rn/config/agw-config";

describe("Agw local config codec", () => {
  it("round-trips a normalized config through Base64URL", () => {
    const encoded = encodeConfigBase64Url({
      version: 1,
      serverDomain: "https://api.example.com/",
      apiKey: " secret-key ",
    });

    expect(encoded).not.toMatch(/[+/=]/);
    expect(parseEncodedConfig(encoded)).toEqual({
      version: 1,
      serverDomain: "https://api.example.com",
      apiKey: "secret-key",
    });
  });

  it("parses alias fields from imported JSON", () => {
    expect(
      parseConfigFileContent(
        JSON.stringify({
          version: 1,
          domain: "http://localhost:5015/",
          api_key: "local-key",
        })
      )
    ).toEqual({
      version: 1,
      serverDomain: "http://localhost:5015",
      apiKey: "local-key",
    });
  });

  it("rejects invalid server domains", () => {
    expect(() =>
      parseConfigFileContent(
        JSON.stringify({
          version: 1,
          serverDomain: "ftp://api.example.com",
          apiKey: "key",
        })
      )
    ).toThrow(AgwConfigError);
  });

  it("ignores whitespace in Base64URL payloads", () => {
    const encoded = encodeConfigBase64Url({
      version: 1,
      serverDomain: "https://api.example.com",
      apiKey: "key",
    });
    const wrapped = `${encoded.slice(0, 8)}\n${encoded.slice(8)}`;

    expect(parseEncodedConfig(wrapped).apiKey).toBe("key");
  });

  it("rejects standard Base64 payloads", () => {
    const standardBase64 = `${encodeConfigBase64Url({
      version: 1,
      serverDomain: "https://api.example.com",
      apiKey: "key",
    })}=`;

    expect(() => parseEncodedConfig(standardBase64)).toThrow(AgwConfigError);
  });
});
