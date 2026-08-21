import { useLocalSearchParams } from "expo-router";
import React from "react";

import { ProfileFormScreen } from "@/features/servers/profile-form-screen";

export default function EditServerScreen(): React.JSX.Element {
  const { profileId } = useLocalSearchParams<{ profileId: string }>();
  return <ProfileFormScreen profileId={profileId} />;
}
