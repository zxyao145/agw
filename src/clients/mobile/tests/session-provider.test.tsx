import { act, fireEvent, render } from "@testing-library/react-native";
import React from "react";
import { Pressable, Text } from "react-native";

import { SessionProvider, useSession } from "@/features/servers/session-provider";
import {
  deleteProfileToken,
  loadProfiles,
  persistProfilesState,
  readProfileToken,
  writeProfileToken,
} from "@/features/servers/profile-store";
import { verifyServerProfile } from "@/features/servers/server-verification";
import type { ServerProfile } from "@/features/servers/types";

jest.mock("@/features/servers/profile-store", () => ({
  deleteProfileToken: jest.fn(),
  emptyProfilesState: () => ({ version: 1, activeProfileId: null, profiles: [] }),
  loadProfiles: jest.fn(),
  persistProfilesState: jest.fn(),
  readProfileToken: jest.fn(),
  writeProfileToken: jest.fn(),
}));
jest.mock("@/features/servers/server-verification", () => ({
  verifyServerProfile: jest.fn(),
}));

const mockLoadProfiles = jest.mocked(loadProfiles);
const mockReadProfileToken = jest.mocked(readProfileToken);
const mockWriteProfileToken = jest.mocked(writeProfileToken);
const mockPersistProfilesState = jest.mocked(persistProfilesState);
const mockDeleteProfileToken = jest.mocked(deleteProfileToken);
const mockVerifyServerProfile = jest.mocked(verifyServerProfile);

const profile: ServerProfile = {
  id: "profile-1",
  name: "Remote",
  serverUrl: "https://agw.example.com",
  apiMajorVersion: 1,
  allowInsecureHttp: false,
};

function SessionProbe(): React.JSX.Element {
  const session = useSession();
  return (
    <>
      <Text testID="status">{session.status}</Text>
      <Text testID="error">{session.error ?? ""}</Text>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel="Save profile"
        onPress={() =>
          void session
            .saveProfile({
              id: profile.id,
              name: profile.name,
              serverUrl: profile.serverUrl,
              token: "agw_new",
              allowInsecureHttp: false,
            })
            .catch(() => undefined)
        }
      />
    </>
  );
}

describe("SessionProvider", () => {
  beforeEach(() => {
    jest.clearAllMocks();
    mockLoadProfiles.mockResolvedValue({
      state: { version: 1, activeProfileId: null, profiles: [profile] },
      migratedProfileId: null,
    });
    mockReadProfileToken.mockResolvedValue("agw_existing");
    mockWriteProfileToken.mockResolvedValue(undefined);
    mockPersistProfilesState.mockResolvedValue(undefined);
    mockDeleteProfileToken.mockResolvedValue(undefined);
  });

  test("does not persist a profile when Server verification fails", async () => {
    mockVerifyServerProfile.mockRejectedValue(
      new Error(
        "Could not connect to the Agw Server. Check the Server URL and network, then try again.",
      ),
    );
    const view = await render(
      <SessionProvider>
        <SessionProbe />
      </SessionProvider>,
    );

    await act(async () => {
      await Promise.resolve();
    });
    await act(async () => {
      fireEvent.press(view.getByLabelText("Save profile"));
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(mockVerifyServerProfile).toHaveBeenCalledTimes(1);
    expect(mockWriteProfileToken).not.toHaveBeenCalled();
    expect(mockPersistProfilesState).not.toHaveBeenCalled();
    expect(view.getByTestId("status").props.children).toBe("error");
    expect(view.getByTestId("error").props.children).toBe(
      "Could not connect to the Agw Server. Check the Server URL and network, then try again.",
    );
  });
});
