type ChatRouteParams = {
  projectId: string | null;
  conversationId: string | null;
};

export function buildChatHref(basePath: string, params: ChatRouteParams): string {
  const searchParams = new URLSearchParams();
  if (params.projectId) {
    searchParams.set("projectId", params.projectId);
  }
  if (params.projectId && params.conversationId) {
    searchParams.set("conversationId", params.conversationId);
  }

  const search = searchParams.toString();
  return `${basePath}/${search ? `?${search}` : ""}`;
}
