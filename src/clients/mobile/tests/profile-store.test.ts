const mockAsyncValues = new Map<string, string>();
const mockSecureValues = new Map<string, string>();

jest.mock("uuid", () => ({ v7: () => "01900000-0000-7000-8000-000000000001" }));
jest.mock("@react-native-async-storage/async-storage", () => ({
  __esModule: true,
  default: {
    getItem: jest.fn((key: string) => Promise.resolve(mockAsyncValues.get(key) ?? null)),
    setItem: jest.fn((key: string, value: string) => {
      mockAsyncValues.set(key, value);
      return Promise.resolve();
    }),
  },
}));
jest.mock("expo-secure-store", () => ({
  getItemAsync: jest.fn((key: string) => Promise.resolve(mockSecureValues.get(key) ?? null)),
  setItemAsync: jest.fn((key: string, value: string) => {
    mockSecureValues.set(key, value);
    return Promise.resolve();
  }),
  deleteItemAsync: jest.fn((key: string) => {
    mockSecureValues.delete(key);
    return Promise.resolve();
  }),
}));

import {
  PROFILES_STATE_KEY,
  getProfileTokenKey,
  loadProfiles,
} from "@/features/servers/profile-store";

describe("profile store", () => {
  beforeEach(() => {
    mockAsyncValues.clear();
    mockSecureValues.clear();
  });

  test("leaves the previous configuration untouched without importing it", async () => {
    const content = JSON.stringify({
      version: 2,
      apiMajorVersion: 1,
      serverUrl: "https://agw.example.com",
      token: "agw_secret",
    });
    mockSecureValues.set("agw.localConfig", content);

    expect(await loadProfiles()).toEqual({ version: 1, activeProfileId: null, profiles: [] });
    expect(mockSecureValues.get("agw.localConfig")).toBe(content);
    expect(mockSecureValues.size).toBe(1);
    expect(mockAsyncValues.size).toBe(0);
  });

  test("loads current profiles without changing their token", async () => {
    const profile = {
      id: "profile-1",
      name: "Server",
      serverUrl: "https://agw.example.com",
      apiMajorVersion: 1,
      allowInsecureHttp: false,
    };
    const state = { version: 1, activeProfileId: profile.id, profiles: [profile] };
    mockAsyncValues.set(PROFILES_STATE_KEY, JSON.stringify(state));
    mockSecureValues.set(getProfileTokenKey(profile.id), "agw_current");

    expect(await loadProfiles()).toEqual(state);
    expect(mockSecureValues.get(getProfileTokenKey(profile.id))).toBe("agw_current");
  });
});
