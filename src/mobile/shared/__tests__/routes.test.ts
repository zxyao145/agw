import { resolveRoute } from "../src/rn/routes";

describe("resolveRoute", () => {
  it("maps native route aliases to the single Agw page", () => {
    expect(resolveRoute("settings")).toEqual({
      routeName: "agw",
      title: "Agw",
      description: "Chat, files, and recent history in one React Native page.",
      accentColor: "#0058bc",
      initialTab: "chat",
    });
  });

  it("can open the files tab as the initial page state", () => {
    expect(resolveRoute("files")?.initialTab).toBe("files");
  });

  it("returns undefined for an unknown route", () => {
    expect(resolveRoute("missing")).toBeUndefined();
  });
});
