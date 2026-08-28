import { render } from "@testing-library/react-native";
import React from "react";

import { useSession } from "@/features/servers/session-provider";
import IndexScreen from "../app/index";

jest.mock("expo-router", () => {
  const mockReact = jest.requireActual<typeof import("react")>("react");
  const { Text } = jest.requireActual<typeof import("react-native")>("react-native");
  return {
    Redirect: ({ href }: { href: string }) =>
      mockReact.createElement(Text, null, `redirect:${href}`),
  };
});
jest.mock("@/features/servers/session-provider", () => ({ useSession: jest.fn() }));

const mockUseSession = jest.mocked(useSession);

describe("IndexScreen", () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  test("redirects a failed startup to Server settings", async () => {
    mockUseSession.mockReturnValue({ status: "error" } as never);

    const view = await render(<IndexScreen />);

    expect(view.getByText("redirect:/settings")).toBeTruthy();
  });

  test("redirects an authenticated startup to Chat", async () => {
    mockUseSession.mockReturnValue({ status: "authenticated" } as never);

    const view = await render(<IndexScreen />);

    expect(view.getByText("redirect:/chat")).toBeTruthy();
  });

  test("redirects an unauthenticated startup to Server settings", async () => {
    mockUseSession.mockReturnValue({ status: "unauthenticated" } as never);

    const view = await render(<IndexScreen />);

    expect(view.getByText("redirect:/settings")).toBeTruthy();
  });

  test.each(["booting", "verifying"] as const)(
    "shows the connection loader while startup is %s",
    async (status) => {
      mockUseSession.mockReturnValue({ status } as never);

      const view = await render(<IndexScreen />);

      expect(view.getByText("Connecting to Agw")).toBeTruthy();
    },
  );
});
