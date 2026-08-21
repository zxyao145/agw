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
  LEGACY_CONFIG_KEY,
  PROFILES_STATE_KEY,
  getProfileTokenKey,
  loadProfiles,
} from "@/features/servers/profile-store";

describe("profile store migration", () => {
  beforeEach(() => {
    mockAsyncValues.clear();
    mockSecureValues.clear();
  });

  test("migrates the legacy HTTPS configuration and activates it", async () => {
    mockSecureValues.set(
      LEGACY_CONFIG_KEY,
      JSON.stringify({
        version: 2,
        apiMajorVersion: 1,
        serverUrl: "https://agw.example.com",
        token: "agw_secret",
      }),
    );

    const loaded = await loadProfiles();
    const profile = loaded.state.profiles[0];

    expect(loaded.state.activeProfileId).toBe(profile.id);
    expect(mockSecureValues.get(getProfileTokenKey(profile.id))).toBe("agw_secret");
    expect(mockSecureValues.has(LEGACY_CONFIG_KEY)).toBe(false);
    expect(mockAsyncValues.has(PROFILES_STATE_KEY)).toBe(true);
  });

  test("migrated HTTP profiles wait for an explicit confirmation", async () => {
    mockSecureValues.set(
      LEGACY_CONFIG_KEY,
      JSON.stringify({
        version: 2,
        apiMajorVersion: 1,
        serverUrl: "http://192.168.1.4:30816",
        token: "agw_secret",
      }),
    );

    const loaded = await loadProfiles();

    expect(loaded.state.activeProfileId).toBeNull();
    expect(loaded.migratedProfileId).toBe(loaded.state.profiles[0].id);
    expect(loaded.state.profiles[0].allowInsecureHttp).toBe(false);
  });
});
