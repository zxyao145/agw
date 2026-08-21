import { Stack } from "expo-router";
import React from "react";

export default function SettingsLayout(): React.JSX.Element {
  return <Stack screenOptions={{ headerShown: false }} />;
}
