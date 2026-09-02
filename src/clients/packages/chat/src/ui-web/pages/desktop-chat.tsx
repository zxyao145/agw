import { ChatRouteBoundary } from "../components/chat/chat-route-boundary";
import { ChatWorkspace } from "./chat/chat-workspace";

export function DesktopChatPage() {
  return (
    <ChatRouteBoundary>
      <ChatWorkspace
        routeBasePath="/desktop/chat"
        showProjectSelect={false}
        compactToolbar
        showUserInputNavigation
      />
    </ChatRouteBoundary>
  );
}
