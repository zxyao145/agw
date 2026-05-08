import type { AgwTabName } from "./pages/home/components/types";

export type RouteName =
  | "agw"
  | "chat"
  | "files"
  | "home"
  | "settings"
  | "details";

export type ReactNativeInitialProps = {
  routeName?: string;
  title?: string;
  source?: string;
};

export type RouteDefinition = {
  routeName: "agw";
  title: string;
  description: string;
  accentColor: string;
  initialTab: AgwTabName;
};

export const routeOrder: RouteName[] = ["agw"];

export const routes: Record<"agw", RouteDefinition> = {
  agw: {
    routeName: "agw",
    title: "Agw",
    description: "Chat, files, and recent history in one React Native page.",
    accentColor: "#0058bc",
    initialTab: "chat",
  },
};

export function resolveRoute(routeName?: string): RouteDefinition | undefined {
  if (
    routeName === undefined ||
    routeName === "agw" ||
    routeName === "chat" ||
    routeName === "home" ||
    routeName === "settings" ||
    routeName === "details"
  ) {
    return routes.agw;
  }

  if (routeName === "files") {
    return { ...routes.agw, initialTab: "files" };
  }

  return undefined;
}
