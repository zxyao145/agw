import { ChatWorkspace } from "@agw/chat";

export function DesktopChatPage() {
  return (
    <ChatWorkspace
      routeBasePath="/desktop/chat"
      showProjectSelect={false}
      compactToolbar
      showUserInputNavigation
    />
  );
}
