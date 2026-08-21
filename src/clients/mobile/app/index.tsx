import { Redirect } from "expo-router";
import React from "react";

import { LoadingView } from "@/components/loading-view";
import { useSession } from "@/features/servers/session-provider";

export default function IndexScreen(): React.JSX.Element {
  const { status } = useSession();
  if (status === "booting" || status === "verifying")
    return <LoadingView label="Connecting to Agw" />;
  return <Redirect href={status === "authenticated" ? "/chat" : "/settings"} />;
}
