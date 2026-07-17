import { ChatWorkspace } from "@/app/(app)/(interface)/chat/chat-workspace";
import { ChatRouteBoundary } from "@/components/chat/chat-route-boundary";

export default function DesktopChatPage() {
  return (
    <ChatRouteBoundary>
      <ChatWorkspace routeBasePath="/desktop/chat" showProjectSelect={false} compactToolbar />
    </ChatRouteBoundary>
  );
}
