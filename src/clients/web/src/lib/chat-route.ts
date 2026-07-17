export type ChatRouteBasePath = "/chat" | "/desktop/chat";

type ChatRouteParams = {
  projectId: string | null;
  contextId: string | null;
};

type ChatRouteRedirectInput = {
  isDesktop: boolean;
  pathname: string;
  search: string;
};

export function buildChatHref(basePath: ChatRouteBasePath, params: ChatRouteParams): string {
  const searchParams = new URLSearchParams();
  if (params.projectId) {
    searchParams.set("projectId", params.projectId);
  }
  if (params.projectId && params.contextId) {
    searchParams.set("contextId", params.contextId);
  }

  const search = searchParams.toString();
  return `${basePath}/${search ? `?${search}` : ""}`;
}

export function getChatRouteRedirect({
  isDesktop,
  pathname,
  search,
}: ChatRouteRedirectInput): string | null {
  const normalizedPathname = pathname.replace(/\/+$/u, "") || "/";
  const nextBasePath =
    isDesktop && normalizedPathname === "/chat"
      ? "/desktop/chat"
      : !isDesktop && normalizedPathname === "/desktop/chat"
        ? "/chat"
        : null;

  if (!nextBasePath) {
    return null;
  }

  const normalizedSearch = search ? (search.startsWith("?") ? search : `?${search}`) : "";
  return `${nextBasePath}/${normalizedSearch}`;
}
