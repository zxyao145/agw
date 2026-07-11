import * as SecureStore from "expo-secure-store";
import {
  deleteLocalConfig,
  readLocalConfig,
  writeLocalConfig,
} from "../src/rn/config/config-store";

jest.mock("expo-secure-store", () => ({
  getItemAsync: jest.fn(),
  setItemAsync: jest.fn(),
  deleteItemAsync: jest.fn(),
}));

const getItemAsyncMock = SecureStore.getItemAsync as jest.MockedFunction<
  typeof SecureStore.getItemAsync
>;
const setItemAsyncMock = SecureStore.setItemAsync as jest.MockedFunction<
  typeof SecureStore.setItemAsync
>;
const deleteItemAsyncMock = SecureStore.deleteItemAsync as jest.MockedFunction<
  typeof SecureStore.deleteItemAsync
>;

describe("config-store", () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it("reads Agw config from Expo SecureStore", async () => {
    getItemAsyncMock.mockResolvedValue(
      JSON.stringify({
        version: 2,
        apiMajorVersion: 1 as const,
        serverUrl: "https://api.example.com/",
        token: "stored-key",
      }),
    );

    await expect(readLocalConfig()).resolves.toEqual({
      version: 2,
      apiMajorVersion: 1 as const,
      serverUrl: "https://api.example.com",
      token: "stored-key",
    });
    expect(getItemAsyncMock).toHaveBeenCalledWith("agw.localConfig");
  });

  it("returns null when no SecureStore config exists", async () => {
    getItemAsyncMock.mockResolvedValue(null);

    await expect(readLocalConfig()).resolves.toBeNull();
  });

  it("writes normalized Agw config to Expo SecureStore", async () => {
    await writeLocalConfig({
      version: 2,
      apiMajorVersion: 1 as const,
      serverUrl: "https://api.example.com",
      token: "stored-key",
    });

    expect(setItemAsyncMock).toHaveBeenCalledTimes(1);
    expect(setItemAsyncMock.mock.calls[0][0]).toBe("agw.localConfig");
    expect(JSON.parse(setItemAsyncMock.mock.calls[0][1])).toEqual({
      version: 2,
      apiMajorVersion: 1 as const,
      serverUrl: "https://api.example.com",
      token: "stored-key",
    });
  });

  it("deletes the Expo SecureStore config key", async () => {
    await deleteLocalConfig();

    expect(deleteItemAsyncMock).toHaveBeenCalledWith("agw.localConfig");
  });
});
